using MicTap.Core;
using MicTap.Hub.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MicTap.Hub;

/// <summary>
/// Where an agent is: in a wait (waiting), out of it for a while (thinking), or gone for longer than
/// <see cref="AgentsState.GoneAfter"/>. Other calls, such as status, do not count as waiting.
/// </summary>
public enum AgentState
{
    Waiting,
    Thinking,
    Gone,
}

/// <summary>An agent of a session as the window shows it.</summary>
public sealed record AgentPresence(string Name, AgentState State, DateTimeOffset LastSeen);

/// <summary>
/// What the hub knows: projects and sessions under the root, the active recording and its language, new segments
/// (to wake waiting agents) and the agents' presence. Thread-safe; fed by the recorder and the pipeline.
/// </summary>
public sealed class AgentsState
{
    public static readonly TimeSpan GoneAfter = TimeSpan.FromMinutes(10);

    private readonly Func<SessionInfo, IReadOnlyList<TranscriptLine>> _readLines;
    private readonly TimeProvider _time;
    private readonly string _version;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Presence> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Waiter>> _waiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pushes = new(StringComparer.OrdinalIgnoreCase);
    private Recording? _active;
    private string _root;

    /// <param name="readLines">The lines of a session as the pipeline has them, including the user's edits.</param>
    public AgentsState(string root, Func<SessionInfo, IReadOnlyList<TranscriptLine>> readLines, TimeProvider time, string version)
    {
        _root = root;
        _readLines = readLines;
        _time = time;
        _version = version;
    }

    /// <summary>An agent connected, left, or started waiting on a session.</summary>
    public event EventHandler? AgentsChanged;

    public string Root
    {
        get
        {
            lock (_gate)
            {
                return _root;
            }
        }

        set
        {
            lock (_gate)
            {
                _root = value;
            }
        }
    }

    public void Attach(Recorder recorder, TranscriptionPipeline pipeline)
    {
        recorder.Started += (_, started) => RecordingStarted(started.Session, started.Language);
        recorder.LanguageMarked += (_, marked) => LanguageChanged(marked.Session, marked.Mark.Lang);
        recorder.Stopped += (_, stopped) => RecordingStopped(stopped.Session);
        pipeline.SegmentTranscribed += (_, segment) => Pulse(segment.Session.Folder);
        pipeline.Pushed += (_, session) => Push(session);
    }

    public void RecordingStarted(SessionInfo session, string language)
    {
        lock (_gate)
        {
            _active = new Recording(session, ProjectName(session), language);
            _pushes.Remove(session.Folder);
        }

        Pulse(session.Folder);
    }

    public void LanguageChanged(SessionInfo session, string language)
    {
        lock (_gate)
        {
            if (_active?.Session == session)
            {
                _active = _active with { Language = language };
            }
        }
    }

    public void RecordingStopped(SessionInfo session)
    {
        lock (_gate)
        {
            if (_active?.Session == session)
            {
                _active = null;
            }

            _pushes.Remove(session.Folder);
        }

        Pulse(session.Folder);
    }

    /// <summary>
    /// The user finished a thought and waits for an answer: every wait on the session ends at once with reason
    /// force_push; with none pending, the next wait does. Only for the session being recorded.
    /// </summary>
    public void Push(SessionInfo session)
    {
        lock (_gate)
        {
            if (_active?.Session != session)
            {
                return;
            }

            if (_waiters.GetValueOrDefault(session.Folder) is { Count: > 0 } waiters)
            {
                waiters.ForEach(w => w.Pushed = true);
            }
            else
            {
                _pushes.Add(session.Folder);
            }
        }

        Pulse(session.Folder);
    }

    /// <summary>Something changed in the session (a new segment, a stop): waiting agents look again.</summary>
    public void Pulse(string sessionFolder)
    {
        TaskCompletionSource? change;
        lock (_gate)
        {
            _changes.Remove(sessionFolder, out change);
        }

        change?.TrySetResult();
    }

    public StatusReply Status()
    {
        Recording? active;
        lock (_gate)
        {
            active = _active;
        }

        return new StatusReply(
            _version,
            active is null ? null : new RecordingDto(active.Project, active.Session.Name, active.Language),
            [ .. AllAgents() ]);
    }

    public IReadOnlyList<ProjectDto> ListProjects() =>
    [
        .. Projects.List(Root).Select(p => new ProjectDto(
            p.Name, p.Settings.Languages, RetentionName(p.Settings.AudioRetention), SessionFolders.List(p.Folder).Count)),
    ];

    public IReadOnlyList<SessionDto> ListSessions(string project) =>
    [
        .. SessionFolders.List(FindProject(project).Folder).Select(s => new SessionDto(
            s.Name, File.GetLastWriteTime(s.MetaFile), _readLines(s).Count)),
    ];

    public IReadOnlyList<SegmentDto> Tail(TailRequest request)
    {
        (_, SessionInfo session) = Resolve(request.Project, request.Session);
        return [ .. _readLines(session).Where(l => l.Seq >= request.FromSeq).Select(ToDto) ];
    }

    /// <summary>
    /// Holds until the agent's condition is met and returns the segments it has not read: at once if there are some
    /// and no condition is given; at <see cref="WaitRequest.MinWords"/> words; at <see cref="WaitRequest.MaxWaitSeconds"/>;
    /// at a <see cref="Push"/>; or when the session is not recording. The agent's cursor moves to the last returned seq.
    /// A newer wait of the same agent on the session takes over: this one then ends without moving the cursor, since
    /// nobody reads it any more (a tool call that gave up while the CLI went on waiting).
    /// </summary>
    public async Task<WaitReply> WaitAsync(WaitRequest request, string connectionId, CancellationToken cancellation)
    {
        (ProjectInfo project, SessionInfo session) = Resolve(request.Project, request.Session);
        using var waiter = new Waiter(request.Agent);
        try
        {
            int cursor = Enter(session, waiter, request.FromStart);
            Joined(request.Agent, connectionId, project, session, cursor);
            DateTimeOffset? deadline = request.MaxWaitSeconds is { } seconds ? _time.GetUtcNow().AddSeconds(seconds) : null;
            bool unconditional = request.MinWords is null && request.MaxWaitSeconds is null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, waiter.TakenOver);

            string reason;
            IReadOnlyList<TranscriptLine> unread;
            while (true)
            {
                Task change = ChangeOf(session.Folder); // taken before reading, so a segment written meanwhile wakes us
                unread = [ .. _readLines(session).Where(l => l.Seq > cursor) ];
                if (waiter.Pushed)
                {
                    reason = WaitReasons.ForcePush;
                    break;
                }

                if (unconditional && unread.Count > 0)
                {
                    reason = WaitReasons.FirstEvent;
                    break;
                }

                if (request.MinWords is { } minWords && unread.Sum(l => WordCount.Of(l.Text)) >= minWords)
                {
                    reason = WaitReasons.MinWords;
                    break;
                }

                if (!IsRecording(session))
                {
                    reason = WaitReasons.SessionStopped;
                    break;
                }

                TimeSpan left = deadline is null ? Timeout.InfiniteTimeSpan : deadline.Value - _time.GetUtcNow();
                if (left <= TimeSpan.Zero && deadline is not null)
                {
                    reason = WaitReasons.MaxWait;
                    break;
                }

                try
                {
                    await change.WaitAsync(left, _time, stop.Token);
                }
                catch (TimeoutException)
                {
                    // the deadline: the loop answers with what has accumulated
                }
                catch (OperationCanceledException) when (waiter.TakenOver.IsCancellationRequested && !cancellation.IsCancellationRequested)
                {
                    throw TakenOver(request.Agent);
                }
            }

            int toSeq = unread.Count > 0 ? unread[ ^1 ].Seq : cursor;
            if (!Commit(session, waiter, new AgentCursor(toSeq, _time.GetUtcNow(), new AgentPolicy(request.MinWords, request.MaxWaitSeconds))))
            {
                throw TakenOver(request.Agent);
            }

            Answered(request.Agent, toSeq);
            return new WaitReply(project.Name, session.Name, reason, cursor + 1, toSeq, LanguageNow(session), [ .. unread.Select(ToDto) ]);
        }
        finally
        {
            Leave(session, waiter);
            Left(request.Agent, connectionId);
        }
    }

    /// <summary>Agents of a session: those in a wait are waiting, the others think until <see cref="GoneAfter"/>, then are gone.</summary>
    public IReadOnlyList<AgentPresence> AgentsOf(SessionInfo session)
    {
        var agents = new Dictionary<string, AgentPresence>(StringComparer.OrdinalIgnoreCase);
        foreach ((string agent, AgentCursor cursor) in AgentCursors.List(session))
        {
            agents[ agent ] = new AgentPresence(agent, StateAt(waiting: false, cursor.LastSeen), cursor.LastSeen);
        }

        lock (_gate)
        {
            foreach ((string agent, Presence presence) in _agents.Where(a => a.Value.Session == session))
            {
                agents[ agent ] = new AgentPresence(agent, StateAt(presence.Waits.Count > 0, presence.LastSeen), presence.LastSeen);
            }
        }

        return [ .. agents.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase) ];
    }

    private IEnumerable<AgentDto> AllAgents()
    {
        lock (_gate)
        {
            return
            [
                .. _agents.Where(a => a.Value.Session is not null).Select(a => new AgentDto(
                    a.Key, a.Value.Project, a.Value.Session!.Name,
                    StateAt(a.Value.Waits.Count > 0, a.Value.LastSeen).ToString().ToLowerInvariant(),
                    a.Value.LastSeen, a.Value.Cursor)),
            ];
        }
    }

    private AgentState StateAt(bool waiting, DateTimeOffset lastSeen) =>
        waiting ? AgentState.Waiting
        : _time.GetUtcNow() - lastSeen > GoneAfter ? AgentState.Gone
        : AgentState.Thinking;

    private void Joined(string agent, string connectionId, ProjectInfo project, SessionInfo session, int cursor)
    {
        lock (_gate)
        {
            Presence presence = PresenceOf(agent);
            presence.Waits.Add(connectionId);
            presence.Project = project.Name;
            presence.Session = session;
            presence.Cursor = cursor;
            presence.LastSeen = _time.GetUtcNow();
        }

        AgentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Answered(string agent, int cursor)
    {
        lock (_gate)
        {
            Presence presence = PresenceOf(agent);
            presence.Cursor = cursor;
            presence.LastSeen = _time.GetUtcNow();
        }

        AgentsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A wait ended: answered, taken over, or its caller went away.</summary>
    private void Left(string agent, string connectionId)
    {
        lock (_gate)
        {
            Presence presence = PresenceOf(agent);
            presence.Waits.Remove(connectionId);
            presence.LastSeen = _time.GetUtcNow();
        }

        AgentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private Presence PresenceOf(string agent)
    {
        if (!_agents.TryGetValue(agent, out Presence? presence))
        {
            presence = new Presence { LastSeen = _time.GetUtcNow() };
            _agents[ agent ] = presence;
        }

        return presence;
    }

    /// <summary>
    /// Registers a wait and reads its cursor: an older wait of the same agent on the session is taken over before it
    /// could write its cursor, and a push made while nobody waited goes to this one.
    /// </summary>
    private int Enter(SessionInfo session, Waiter waiter, bool fromStart)
    {
        Waiter? older;
        int cursor;
        lock (_gate)
        {
            cursor = fromStart ? 0 : AgentCursors.Read(session, waiter.Agent)?.Cursor ?? 0;
            if (!_waiters.TryGetValue(session.Folder, out List<Waiter>? waiters))
            {
                waiters = [];
                _waiters[ session.Folder ] = waiters;
            }

            older = waiters.Find(w => string.Equals(w.Agent, waiter.Agent, StringComparison.OrdinalIgnoreCase));
            if (older is not null)
            {
                waiters.Remove(older); // from here on it cannot commit
            }

            waiter.Pushed = _pushes.Remove(session.Folder);
            waiters.Add(waiter);
        }

        older?.TakeOver(); // outside the lock: its wait wakes up and ends
        return cursor;
    }

    /// <summary>Writes the cursor of a wait that answers; false when a newer wait has taken it over.</summary>
    private bool Commit(SessionInfo session, Waiter waiter, AgentCursor cursor)
    {
        lock (_gate)
        {
            if (_waiters.GetValueOrDefault(session.Folder)?.Remove(waiter) != true)
            {
                return false;
            }

            AgentCursors.Write(session, waiter.Agent, cursor);
            return true;
        }
    }

    private void Leave(SessionInfo session, Waiter waiter)
    {
        lock (_gate)
        {
            if (_waiters.GetValueOrDefault(session.Folder) is { } waiters)
            {
                waiters.Remove(waiter);
                if (waiters.Count == 0)
                {
                    _waiters.Remove(session.Folder);
                }
            }
        }
    }

    private static HubException TakenOver(string agent) =>
        new($"{AgentHub.Superseded} a newer `mictap wait --agent {agent}` on this session took over; this call returns nothing and leaves the cursor alone");

    private Task ChangeOf(string sessionFolder)
    {
        lock (_gate)
        {
            if (!_changes.TryGetValue(sessionFolder, out TaskCompletionSource? change))
            {
                change = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _changes[ sessionFolder ] = change;
            }

            return change.Task;
        }
    }

    private bool IsRecording(SessionInfo session)
    {
        lock (_gate)
        {
            return _active?.Session == session;
        }
    }

    private string? LanguageNow(SessionInfo session)
    {
        lock (_gate)
        {
            if (_active?.Session == session)
            {
                return _active.Language;
            }
        }

        return File.Exists(session.MetaFile) ? SessionMeta.Read(session, "ru").LanguageMarks[ ^1 ].Lang : null;
    }

    /// <summary>
    /// The session meant: the active recording when nothing is named; a session name alone is looked up in every
    /// project and must be unique.
    /// </summary>
    private (ProjectInfo Project, SessionInfo Session) Resolve(string? project, string? session)
    {
        if (project is null && session is null)
        {
            Recording? active;
            lock (_gate)
            {
                active = _active;
            }

            return active is null
                ? throw new HubException($"{AgentHub.NotFound} no recording is running; name --project and --session")
                : (Projects.Load(Path.GetDirectoryName(active.Session.Folder)!), active.Session);
        }

        if (session is null)
        {
            // A project alone: its running recording.
            ProjectInfo named = FindProject(project!);
            Recording? active;
            lock (_gate)
            {
                active = _active;
            }

            return active is not null && SameFolder(Path.GetDirectoryName(active.Session.Folder)!, named.Folder)
                ? (named, active.Session)
                : throw new HubException($"{AgentHub.NotFound} project '{named.Name}' is not recording; name --session");
        }

        IEnumerable<ProjectInfo> candidates = project is null ? Projects.List(Root) : [ FindProject(project) ];
        var matches = candidates
            .Select(p => (Project: p, Session: new SessionInfo(session, Path.Combine(p.Folder, session))))
            .Where(m => File.Exists(m.Session.MetaFile))
            .ToList();
        return matches.Count switch
        {
            1 => matches[ 0 ],
            0 => throw new HubException($"{AgentHub.NotFound} no session '{session}'{(project is null ? "" : $" in project '{project}'")}"),
            _ => throw new HubException(
                $"{AgentHub.NotFound} session '{session}' is in several projects ({string.Join(", ", matches.Select(m => m.Project.Name))}); name --project"),
        };
    }

    private ProjectInfo FindProject(string name) =>
        Projects.List(Root).FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(p.Folder), name, StringComparison.OrdinalIgnoreCase))
        ?? throw new HubException($"{AgentHub.NotFound} no project '{name}'");

    private static bool SameFolder(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string ProjectName(SessionInfo session)
    {
        string folder = Path.GetDirectoryName(session.Folder)!;
        return File.Exists(Path.Combine(folder, Projects.FileName)) ? Projects.Load(folder).Name : Path.GetFileName(folder);
    }

    private static string RetentionName(AudioRetention retention) => retention switch
    {
        AudioRetention.Keep => "keep",
        AudioRetention.DeleteAfterTranscript => "deleteAfterTranscript",
        _ => "none",
    };

    private static SegmentDto ToDto(TranscriptLine line) => new(line.Seq, line.Start, line.End, line.Lang, line.Text);

    private sealed record Recording(SessionInfo Session, string Project, string Language);

    /// <summary>A wait in progress: its agent, whether a push reached it, and the signal that a newer wait took over.</summary>
    private sealed class Waiter(string agent) : IDisposable
    {
        private readonly CancellationTokenSource _takenOver = new();

        private bool _pushed;

        public string Agent { get; } = agent;

        public bool Pushed
        {
            get => Volatile.Read(ref _pushed);
            set => Volatile.Write(ref _pushed, value);
        }

        public CancellationToken TakenOver => _takenOver.Token;

        public void TakeOver() => _takenOver.Cancel();

        public void Dispose() => _takenOver.Dispose();
    }

    private sealed class Presence
    {
        /// <summary>Connections with a wait open.</summary>
        public readonly HashSet<string> Waits = [];
        public string Project = "";
        public SessionInfo? Session;
        public DateTimeOffset LastSeen;
        public int Cursor;
    }
}

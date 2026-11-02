using MicTap.Core;
using MicTap.Hub.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MicTap.Hub;

/// <summary>
/// Where an agent is: in a wait (waiting); out of it for up to <see cref="AgentsState.ThinkingFor"/> (thinking over what
/// it got, about to wait again); then away — it may come back, but nothing says it is still working; gone after
/// <see cref="AgentsState.GoneAfter"/>. Other calls, such as status, do not count as waiting.
/// </summary>
public enum AgentState
{
    Waiting,
    Thinking,
    Away,
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

    /// <summary>
    /// How long an agent out of a wait is called thinking. Up to <see cref="GoneAfter"/> it still counts as present (it
    /// may be writing a long answer: nothing tells), but is shown as away: 10 minutes of «думает» outlived a closed chat.
    /// </summary>
    public static readonly TimeSpan ThinkingFor = TimeSpan.FromMinutes(2);

    private const string RecordingChanges = "\0recordings";

    private readonly Func<SessionInfo, IReadOnlyList<TranscriptLine>> _readLines;
    private readonly TimeProvider _time;
    private readonly string _version;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Presence> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Waiter>> _waiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Waiter>> _startWaiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Transcribing> _transcribing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _startAnchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _renamePending = new(StringComparer.OrdinalIgnoreCase);
    private Recording? _active;
    private bool _transcriptionFailed;
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
        recorder.Stopped += (_, stopped) => RecordingStopped(stopped.Session, transcribing: true);
        pipeline.SegmentTranscribed += (_, segment) => Pulse(segment.Session.Folder);
        pipeline.ManualTextAdded += (_, segment) => Pulse(segment.Session.Folder);
        pipeline.Pushed += (_, session) => Push(session);
        pipeline.RecordingTranscribed += (_, session) => Transcribed(session);
        pipeline.Failed += (_, _) => TranscriptionFailed();
    }

    public void RecordingStarted(SessionInfo session, string language)
    {
        int anchor = _readLines(session).Select(line => line.Seq).DefaultIfEmpty(0).Max();
        lock (_gate)
        {
            _active = new Recording(session, ProjectName(session), language);
            _startAnchors[ session.Folder ] = anchor;
            _pushes.Remove(session.Folder);
        }

        Pulse(session.Folder);
        Pulse(RecordingChanges);
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

    /// <param name="transcribing">
    /// The pipeline still recognizes the last phrases of the recording: waits end with session_stopped only once
    /// <see cref="Transcribed"/> says they are in the transcript, so the agent gets all of them.
    /// </param>
    public void RecordingStopped(SessionInfo session, bool transcribing = false)
    {
        lock (_gate)
        {
            if (_active?.Session == session)
            {
                _active = null;
            }

            _pushes.Remove(session.Folder);
            if (transcribing && !_transcriptionFailed)
            {
                CountTranscribing(session, 1);
            }
        }

        Pulse(session.Folder);
        Pulse(RecordingChanges);
    }

    /// <summary>Every phrase of a stopped recording is in the transcript. May come before <see cref="RecordingStopped"/>.</summary>
    public void Transcribed(SessionInfo session)
    {
        lock (_gate)
        {
            CountTranscribing(session, -1);
        }

        Pulse(session.Folder);
        Pulse(RecordingChanges);
    }

    /// <summary>Recognition stopped for good: no more phrases will come, so no wait holds for them.</summary>
    public void TranscriptionFailed()
    {
        string[] folders;
        lock (_gate)
        {
            _transcriptionFailed = true;
            folders = [ .. _transcribing.Keys ];
            _transcribing.Clear();
        }

        foreach (string folder in folders)
        {
            Pulse(folder);
        }

        Pulse(RecordingChanges);
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

            SetPush(session);
        }

        Pulse(session.Folder);
    }

    /// <summary>
    /// The user sent text by hand and asked for an answer: waits on the session end with force_push without a
    /// recording. Useful after session_stopped, when the next recording has not begun yet.
    /// </summary>
    public void PushText(SessionInfo session)
    {
        lock (_gate)
        {
            SetPush(session);
        }

        Pulse(session.Folder);
    }

    private void SetPush(SessionInfo session)
    {
        if (_waiters.GetValueOrDefault(session.Folder) is { Count: > 0 } waiters)
        {
            waiters.ForEach(w => w.Pushed = true);
        }
        else
        {
            _pushes.Add(session.Folder);
        }
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

    /// <summary>
    /// A rename was promised during a recording: waits under this project hold until the folder actually moves, so a
    /// listening CLI gets project_moved instead of session_stopped for a project whose name it no longer has.
    /// </summary>
    public void ProjectRenamePending(string projectFolder)
    {
        lock (_gate)
        {
            _renamePending.Add(projectFolder);
        }

        Pulse(RecordingChanges);
    }

    /// <summary>The promised rename did not happen: waits under the project are released at once.</summary>
    public void ProjectRenameCancelled(string projectFolder)
    {
        string[] folders;
        lock (_gate)
        {
            _renamePending.Remove(projectFolder);
            folders = [ .. _waiters.Keys.Where(folder => IsUnder(folder, projectFolder)) ];
        }

        foreach (string folder in folders)
        {
            Pulse(folder);
        }

        Pulse(RecordingChanges);
    }

    /// <summary>
    /// A project folder moved: the active recording, waiters, presence and per-session signals move with it. Agents
    /// waiting there get reason project_moved with both names and folders, and keep their cursors for the new place.
    /// </summary>
    public void ProjectRenamed(string oldFolder, string newFolder, string oldName, string newName)
    {
        var move = new ProjectMoveDto(oldName, newName, oldFolder, newFolder);
        List<string> folders = [];
        List<TaskCompletionSource> changed = [];
        lock (_gate)
        {
            _renamePending.Remove(oldFolder);
            foreach ((string key, TaskCompletionSource change) in _changes.Where(entry => IsUnder(entry.Key, oldFolder)).ToList())
            {
                _changes.Remove(key);
                changed.Add(change);
            }

            if (_active is { } active && IsUnder(active.Session.Folder, oldFolder))
            {
                _active = new Recording(
                    new SessionInfo(active.Session.Name, Moved(active.Session.Folder, oldFolder, newFolder)), newName, active.Language);
            }

            MoveKeys(_transcribing, oldFolder, newFolder, value => value with
            {
                Session = new SessionInfo(value.Session.Name, Moved(value.Session.Folder, oldFolder, newFolder)),
            });
            MoveSet(_pushes, oldFolder, newFolder);
            MoveKeys(_startAnchors, oldFolder, newFolder, value => value);
            MoveWaiters(oldFolder, newFolder, folders, move);
            MoveStartWaiters(oldFolder, newFolder, oldName, move);
            foreach (Presence presence in _agents.Values.Where(presence => presence.Session is not null && IsUnder(presence.Session.Folder, oldFolder)))
            {
                presence.Project = newName;
                presence.Session = new SessionInfo(presence.Session!.Name, Moved(presence.Session.Folder, oldFolder, newFolder));
            }
        }

        changed.ForEach(change => change.TrySetResult());
        foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Pulse(folder);
        }

        Pulse(RecordingChanges);
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

    public TailReply Tail(TailRequest request)
    {
        (ProjectInfo project, SessionInfo session) = Resolve(request.Project, request.Session);
        SessionMeta? meta = MetaOf(session);
        return new TailReply(
            project.Name, session.Name, meta?.Started, [ .. _readLines(session).Where(l => l.Seq >= request.FromSeq).Select(l => ToDto(l, meta)) ]);
    }

    /// <summary>
    /// Holds until the agent's condition is met and returns the segments it has not read: at once if there are some
    /// and no condition is given; at <see cref="WaitRequest.MinWords"/> words; at <see cref="WaitRequest.MaxWaitSeconds"/>;
    /// at a <see cref="Push"/>; or when the session is not recording and its last phrases are in. The agent's cursor
    /// moves to the last returned seq. With <see cref="WaitRequest.WaitForStart"/> no running recording is required:
    /// the wait begins when the next matching recording starts.
    /// A newer wait of the same agent on the session takes over: this one then ends without moving the cursor, since
    /// nobody reads it any more (a tool call that gave up while the CLI went on waiting).
    /// </summary>
    public async Task<WaitReply> WaitAsync(WaitRequest request, string connectionId, CancellationToken cancellation)
    {
        using var waiter = new Waiter(request.Agent);
        SessionInfo? session = null;
        ProjectInfo? project = null;
        bool waitedForStart = false;
        try
        {
            if (request.WaitForStart)
            {
                waitedForStart = true;
                if (ActiveMatching(request) is { } active)
                {
                    (project, session) = (Projects.Load(Path.GetDirectoryName(active.Folder)!), active);
                }
                else
                {
                    EnterStart(request, waiter);
                    (project, session) = await WaitForStartAsync(request, waiter, cancellation);
                }
            }
            else
            {
                (project, session) = Resolve(request.Project, request.Session);
            }

            if (waiter.Move is { } earlyMove)
            {
                return MovedReply(project?.Name ?? earlyMove.NewName, session?.Name ?? request.Session ?? "", earlyMove, 0);
            }

            int? backlogEnd = request.SkipBacklog ? BacklogEnd(session!) : null;
            int cursor = Enter(session!, waiter, request.FromStart, backlogEnd, waitedForStart);
            Joined(request.Agent, connectionId, project!, session!, cursor);
            DateTimeOffset? deadline = request.MaxWaitSeconds is { } seconds ? _time.GetUtcNow().AddSeconds(seconds) : null;
            bool unconditional = request.MinWords is null && request.MaxWaitSeconds is null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, waiter.TakenOver);

            string reason;
            IReadOnlyList<TranscriptLine> unread;
            while (true)
            {
                Task change = ChangeOf(session.Folder); // taken before reading, so a segment written meanwhile wakes us
                unread = [ .. _readLines(session).Where(l => l.Seq > cursor) ];
                if (waiter.Move is { } move)
                {
                    SessionInfo moved = waiter.Session ?? new SessionInfo(session.Name, Path.Combine(move.NewPath, session.Name));
                    if (Commit(moved, waiter, new AgentCursor(cursor, _time.GetUtcNow(), new AgentPolicy(request.MinWords, request.MaxWaitSeconds))))
                    {
                        Answered(request.Agent, cursor);
                        return MovedReply(move.NewName, moved.Name, move, cursor);
                    }

                    throw TakenOver(request.Agent);
                }

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

                if (!IsGoingOn(session))
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
            SessionMeta? meta = MetaOf(session);
            return new WaitReply(
                project.Name, session.Name, meta?.Started, reason, cursor + 1, toSeq, LanguageNow(session), [ .. unread.Select(l => ToDto(l, meta)) ]);
        }
        finally
        {
            SessionInfo? current = waiter.Session ?? session;
            if (current is not null)
            {
                Leave(current, waiter);
            }

            LeaveStart(waiter);
            if (session is not null)
            {
                Left(request.Agent, connectionId);
            }
        }
    }

    /// <summary>Agents of a session: waiting, thinking, away or gone; see <see cref="AgentState"/>.</summary>
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

    private AgentState StateAt(bool waiting, DateTimeOffset lastSeen)
    {
        TimeSpan since = _time.GetUtcNow() - lastSeen;
        return waiting ? AgentState.Waiting
            : since <= ThinkingFor ? AgentState.Thinking
            : since <= GoneAfter ? AgentState.Away
            : AgentState.Gone;
    }

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
    /// For --skip-backlog and a wait that began before the recording: the last seq that existed when the recording now
    /// going on began — what is said in it still comes, however late the listener joins; all lines when nothing runs.
    /// The anchor is a seq, not a time: a text session starts at 0 s like the recording appended to it.
    /// </summary>
    private int BacklogEnd(SessionInfo session)
    {
        if (!IsGoingOn(session))
        {
            return _readLines(session).Select(line => line.Seq).DefaultIfEmpty(0).Max();
        }

        lock (_gate)
        {
            return _startAnchors.GetValueOrDefault(session.Folder);
        }
    }

    /// <summary>
    /// Registers a wait and reads its cursor: an older wait of the same agent on the session is taken over before it
    /// could write its cursor, and a push made while nobody waited goes to this one.
    /// </summary>
    /// <param name="startAnchor">
    /// A wait that began before the recording: with no earlier cursor it starts where the new recording starts, not
    /// with the session's old text.
    /// </param>
    private int Enter(SessionInfo session, Waiter waiter, bool fromStart, int? backlogEnd, bool startAnchor = false)
    {
        Waiter? older;
        int cursor;
        int startCursor = startAnchor ? BacklogEnd(session) : 0;
        lock (_gate)
        {
            cursor = fromStart ? 0
                : AgentCursors.Read(session, waiter.Agent)?.Cursor
                ?? backlogEnd
                ?? startCursor;
            waiter.Session = session;
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

    /// <summary>Waits for the next recording that matches the request; project_moved ends it with no recording.</summary>
    private async Task<(ProjectInfo Project, SessionInfo Session)> WaitForStartAsync(
        WaitRequest request, Waiter waiter, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, waiter.TakenOver);
        while (true)
        {
            Task change = ChangeOf(RecordingChanges);
            if (waiter.Move is { } move)
            {
                return (
                    new ProjectInfo(move.NewPath, new ProjectSettings(move.NewName, [ "ru" ], AudioRetention.Keep)),
                    new SessionInfo(request.Session ?? "", Path.Combine(move.NewPath, request.Session ?? "")));
            }

            if (ActiveMatching(request) is { } active)
            {
                if (!LeaveStart(waiter))
                {
                    throw TakenOver(request.Agent);
                }

                return (Projects.Load(Path.GetDirectoryName(active.Folder)!), active);
            }

            try
            {
                await change.WaitAsync(Timeout.InfiniteTimeSpan, _time, stop.Token);
            }
            catch (OperationCanceledException) when (waiter.TakenOver.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                throw TakenOver(request.Agent);
            }
        }
    }

    /// <summary>A recording going on that matches where the wait looks; null until one starts.</summary>
    private SessionInfo? ActiveMatching(WaitRequest request)
    {
        lock (_gate)
        {
            if (_active is not { } active)
            {
                return null;
            }

            bool sameProject = request.Project is null
                || string.Equals(active.Project, request.Project, StringComparison.OrdinalIgnoreCase)
                || SameFolder(Path.GetDirectoryName(active.Session.Folder)!, request.Project);
            bool sameSession = request.Session is null || string.Equals(active.Session.Name, request.Session, StringComparison.OrdinalIgnoreCase);
            return sameProject && sameSession ? active.Session : null;
        }
    }

    private static string StartKey(WaitRequest request) =>
        request.Project is { } project && request.Session is { } session ? $"{project}\n{session}"
        : request.Project is { } onlyProject ? onlyProject
        : request.Session is { } onlySession ? $"\n{onlySession}"
        : "*";

    /// <summary>Registers a wait for the next recording; a newer wait of the same agent for the same target takes over.</summary>
    private void EnterStart(WaitRequest request, Waiter waiter)
    {
        Waiter? older;
        lock (_gate)
        {
            string key = StartKey(request);
            if (!_startWaiters.TryGetValue(key, out List<Waiter>? waiters))
            {
                waiters = [];
                _startWaiters[ key ] = waiters;
            }

            older = waiters.Find(w => string.Equals(w.Agent, waiter.Agent, StringComparison.OrdinalIgnoreCase));
            if (older is not null)
            {
                waiters.Remove(older);
            }

            waiters.Add(waiter);
        }

        older?.TakeOver();
    }

    /// <summary>Removes a wait for the next recording; false when a newer one has taken it over.</summary>
    private bool LeaveStart(Waiter waiter)
    {
        lock (_gate)
        {
            foreach (List<Waiter> waiters in _startWaiters.Values)
            {
                if (waiters.Remove(waiter))
                {
                    return true;
                }
            }
        }

        return !waiter.TakenOver.IsCancellationRequested;
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

    private static WaitReply MovedReply(string project, string session, ProjectMoveDto move, int cursor, int? toSeq = null)
    {
        int from = cursor + 1;
        return new WaitReply(
            project, session, null, WaitReasons.ProjectMoved, from, toSeq ?? cursor, null, [], move);
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

    /// <summary>Recording, or stopped while its last phrases are still being recognized.</summary>
    private bool IsGoingOn(SessionInfo session)
    {
        lock (_gate)
        {
            return _active?.Session == session
                || _transcribing.GetValueOrDefault(session.Folder)?.Pending > 0
                || _renamePending.Any(folder => IsUnder(session.Folder, folder));
        }
    }

    /// <summary>The recording going on, see <see cref="IsGoingOn"/>.</summary>
    private SessionInfo? CurrentSession()
    {
        lock (_gate)
        {
            return _active?.Session ?? _transcribing.Values.FirstOrDefault(t => t.Pending > 0)?.Session;
        }
    }

    /// <summary>
    /// Stops and their transcripts finished, counted: either may come first, the session is still being recognized
    /// while there are more stops. Called under the lock.
    /// </summary>
    private void CountTranscribing(SessionInfo session, int change)
    {
        int pending = (_transcribing.GetValueOrDefault(session.Folder)?.Pending ?? 0) + change;
        if (pending == 0)
        {
            _transcribing.Remove(session.Folder);
        }
        else
        {
            _transcribing[ session.Folder ] = new Transcribing(session, pending);
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
    /// The session meant: the recording going on when nothing is named (see <see cref="IsGoingOn"/>); a session name
    /// alone is looked up in every project and must be unique.
    /// </summary>
    private (ProjectInfo Project, SessionInfo Session) Resolve(string? project, string? session)
    {
        if (project is null && session is null)
        {
            return CurrentSession() is { } current
                ? (Projects.Load(Path.GetDirectoryName(current.Folder)!), current)
                : throw new HubException(
                    $"{AgentHub.NotFound} no recording is running; name --project and --session, or use --wait-for-start");
        }

        if (session is null)
        {
            // A project alone: its recording going on.
            ProjectInfo named = FindProject(project!);
            return CurrentSession() is { } current && SameFolder(Path.GetDirectoryName(current.Folder)!, named.Folder)
                ? (named, current)
                : throw new HubException(
                    $"{AgentHub.NotFound} project '{named.Name}' is not recording; name --session, or use --wait-for-start");
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

    private static bool IsUnder(string folder, string parent) =>
        folder.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Moved(string folder, string oldParent, string newParent) =>
        Path.Combine(newParent, Path.GetRelativePath(oldParent, folder));

    private void MoveKeys<T>(Dictionary<string, T> values, string oldFolder, string newFolder, Func<T, T> moved)
    {
        foreach ((string key, T value) in values.Where(entry => IsUnder(entry.Key, oldFolder)).ToList())
        {
            values.Remove(key);
            values[ Moved(key, oldFolder, newFolder) ] = moved(value);
        }
    }

    private static void MoveSet(HashSet<string> values, string oldFolder, string newFolder)
    {
        foreach (string value in values.Where(value => IsUnder(value, oldFolder)).ToList())
        {
            values.Remove(value);
            values.Add(Moved(value, oldFolder, newFolder));
        }
    }

    private void MoveWaiters(string oldFolder, string newFolder, List<string> folders, ProjectMoveDto move)
    {
        foreach ((string folder, List<Waiter> waiters) in _waiters.Where(entry => IsUnder(entry.Key, oldFolder)).ToList())
        {
            _waiters.Remove(folder);
            string target = Moved(folder, oldFolder, newFolder);
            if (!_waiters.TryGetValue(target, out List<Waiter>? movedWaiters))
            {
                movedWaiters = [];
                _waiters[ target ] = movedWaiters;
            }

            foreach (Waiter waiter in waiters)
            {
                waiter.Move = move;
                waiter.Session = waiter.Session is null
                    ? new SessionInfo(Path.GetFileName(folder), target)
                    : new SessionInfo(waiter.Session.Name, Moved(waiter.Session.Folder, oldFolder, newFolder));
                movedWaiters.Add(waiter);
            }

            folders.Add(target);
        }
    }

    private void MoveStartWaiters(string oldFolder, string newFolder, string oldName, ProjectMoveDto move)
    {
        string oldFolderName = Path.GetFileName(oldFolder);
        foreach ((string key, List<Waiter> waiters) in _startWaiters.ToList())
        {
            bool belongs = SameFolder(key, oldFolder)
                || string.Equals(key, oldName, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(oldFolderName + "\n", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(oldName + "\n", StringComparison.OrdinalIgnoreCase);
            if (!belongs)
            {
                continue;
            }

            _startWaiters.Remove(key);
            string target = key
                .Replace(oldFolderName, Path.GetFileName(newFolder), StringComparison.OrdinalIgnoreCase)
                .Replace(oldName, move.NewName, StringComparison.OrdinalIgnoreCase);
            if (!_startWaiters.TryGetValue(target, out List<Waiter>? movedWaiters))
            {
                movedWaiters = [];
                _startWaiters[ target ] = movedWaiters;
            }

            foreach (Waiter waiter in waiters)
            {
                waiter.Move = move;
            }

            movedWaiters.AddRange(waiters);
        }
    }

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

    private static SegmentDto ToDto(TranscriptLine line, SessionMeta? meta) =>
        new(line.Seq, line.Start, line.End, meta?.At(line.Start), meta?.At(line.End), line.Lang, line.Text);

    /// <summary>The session's meta.json, for the clock times of its phrases; null for a session without one.</summary>
    private static SessionMeta? MetaOf(SessionInfo session) => File.Exists(session.MetaFile) ? SessionMeta.Read(session, "ru") : null;

    private sealed record Recording(SessionInfo Session, string Project, string Language);

    /// <summary>A stopped recording whose last phrases are being recognized while <paramref name="Pending"/> is above zero.</summary>
    private sealed record Transcribing(SessionInfo Session, int Pending);

    /// <summary>A wait in progress: its agent, whether a push reached it, and the signal that a newer wait took over.</summary>
    private sealed class Waiter(string agent) : IDisposable
    {
        private readonly CancellationTokenSource _takenOver = new();

        private bool _pushed;

        public string Agent { get; } = agent;

        public SessionInfo? Session { get; set; }

        public ProjectMoveDto? Move { get; set; }

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

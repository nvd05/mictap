using MicTap.Core;
using MicTap.Hub.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MicTap.Hub;

/// <summary>Where an agent is: connected (waiting), out for a while (thinking), or gone for longer than <see cref="AgentsState.GoneAfter"/>.</summary>
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
    }

    public void RecordingStarted(SessionInfo session, string language)
    {
        lock (_gate)
        {
            _active = new Recording(session, ProjectName(session), language);
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
            [.. AllAgents()]);
    }

    public IReadOnlyList<ProjectDto> ListProjects() =>
        [.. Projects.List(Root).Select(p => new ProjectDto(
            p.Name, p.Settings.Languages, RetentionName(p.Settings.AudioRetention), SessionFolders.List(p.Folder).Count))];

    public IReadOnlyList<SessionDto> ListSessions(string project) =>
        [.. SessionFolders.List(FindProject(project).Folder).Select(s => new SessionDto(
            s.Name, File.GetLastWriteTime(s.MetaFile), _readLines(s).Count))];

    public IReadOnlyList<SegmentDto> Tail(TailRequest request)
    {
        (_, SessionInfo session) = Resolve(request.Project, request.Session);
        return [.. _readLines(session).Where(l => l.Seq >= request.FromSeq).Select(ToDto)];
    }

    /// <summary>
    /// Holds until the agent's condition is met and returns the segments it has not read: at once if there are some
    /// and no condition is given; at <see cref="WaitRequest.MinWords"/> words; at <see cref="WaitRequest.MaxWaitSeconds"/>;
    /// or when the session is not recording. The agent's cursor moves to the last returned seq.
    /// </summary>
    public async Task<WaitReply> WaitAsync(WaitRequest request, string connectionId, CancellationToken cancellation)
    {
        (ProjectInfo project, SessionInfo session) = Resolve(request.Project, request.Session);
        int cursor = request.FromStart ? 0 : AgentCursors.Read(session, request.Agent)?.Cursor ?? 0;
        Joined(request.Agent, connectionId, project, session, cursor);
        DateTimeOffset? deadline = request.MaxWaitSeconds is { } seconds ? _time.GetUtcNow().AddSeconds(seconds) : null;
        bool unconditional = request.MinWords is null && request.MaxWaitSeconds is null;

        string reason;
        IReadOnlyList<TranscriptLine> unread;
        while (true)
        {
            Task change = ChangeOf(session.Folder); // taken before reading, so a segment written meanwhile wakes us
            unread = [.. _readLines(session).Where(l => l.Seq > cursor)];
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
                await change.WaitAsync(left, _time, cancellation);
            }
            catch (TimeoutException)
            {
                // the deadline: the loop answers with what has accumulated
            }
        }

        int toSeq = unread.Count > 0 ? unread[^1].Seq : cursor;
        AgentCursors.Write(session, request.Agent, new AgentCursor(
            toSeq, _time.GetUtcNow(), new AgentPolicy(request.MinWords, request.MaxWaitSeconds)));
        Answered(request.Agent, toSeq);
        return new WaitReply(project.Name, session.Name, reason, cursor + 1, toSeq, LanguageNow(session), [.. unread.Select(ToDto)]);
    }

    public void Connected(string agent, string connectionId)
    {
        lock (_gate)
        {
            PresenceOf(agent).Connections.Add(connectionId);
        }

        AgentsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnected(string agent, string connectionId)
    {
        lock (_gate)
        {
            Presence presence = PresenceOf(agent);
            presence.Connections.Remove(connectionId);
            presence.LastSeen = _time.GetUtcNow();
        }

        AgentsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Agents of a session: connected ones wait, the others think until <see cref="GoneAfter"/>, then are gone.</summary>
    public IReadOnlyList<AgentPresence> AgentsOf(SessionInfo session)
    {
        var agents = new Dictionary<string, AgentPresence>(StringComparer.OrdinalIgnoreCase);
        foreach ((string agent, AgentCursor cursor) in AgentCursors.List(session))
        {
            agents[agent] = new AgentPresence(agent, StateAt(connected: false, cursor.LastSeen), cursor.LastSeen);
        }

        lock (_gate)
        {
            foreach ((string agent, Presence presence) in _agents.Where(a => a.Value.Session == session))
            {
                bool connected = presence.Connections.Count > 0;
                agents[agent] = new AgentPresence(agent, StateAt(connected, presence.LastSeen), presence.LastSeen);
            }
        }

        return [.. agents.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private IEnumerable<AgentDto> AllAgents()
    {
        lock (_gate)
        {
            return [.. _agents.Where(a => a.Value.Session is not null).Select(a => new AgentDto(
                a.Key, a.Value.Project, a.Value.Session!.Name,
                StateAt(a.Value.Connections.Count > 0, a.Value.LastSeen).ToString().ToLowerInvariant(),
                a.Value.LastSeen, a.Value.Cursor))];
        }
    }

    private AgentState StateAt(bool connected, DateTimeOffset lastSeen) =>
        connected ? AgentState.Waiting
        : _time.GetUtcNow() - lastSeen > GoneAfter ? AgentState.Gone
        : AgentState.Thinking;

    private void Joined(string agent, string connectionId, ProjectInfo project, SessionInfo session, int cursor)
    {
        lock (_gate)
        {
            Presence presence = PresenceOf(agent);
            presence.Connections.Add(connectionId);
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

    private Presence PresenceOf(string agent)
    {
        if (!_agents.TryGetValue(agent, out Presence? presence))
        {
            presence = new Presence { LastSeen = _time.GetUtcNow() };
            _agents[agent] = presence;
        }

        return presence;
    }

    private Task ChangeOf(string sessionFolder)
    {
        lock (_gate)
        {
            if (!_changes.TryGetValue(sessionFolder, out TaskCompletionSource? change))
            {
                change = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _changes[sessionFolder] = change;
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

        return File.Exists(session.MetaFile) ? SessionMeta.Read(session, "ru").LanguageMarks[^1].Lang : null;
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
            throw new HubException($"{AgentHub.NotFound} --session is required with --project");
        }

        IEnumerable<ProjectInfo> candidates = project is null ? Projects.List(Root) : [FindProject(project)];
        var matches = candidates
            .Select(p => (Project: p, Session: new SessionInfo(session, Path.Combine(p.Folder, session))))
            .Where(m => File.Exists(m.Session.MetaFile))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
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

    private sealed class Presence
    {
        public readonly HashSet<string> Connections = [];
        public string Project = "";
        public SessionInfo? Session;
        public DateTimeOffset LastSeen;
        public int Cursor;
    }
}

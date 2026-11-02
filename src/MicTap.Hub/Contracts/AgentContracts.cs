// Shared by the hub and the mictap CLI (compiled into both): what goes over SignalR. No other dependencies.
namespace MicTap.Hub.Contracts;

public static class AgentHub
{
    public const string Path = "/agents";

    /// <summary>Query parameter with the agent's name; presence and cursors are per agent.</summary>
    public const string AgentQuery = "agent";

    public const int DefaultPort = 47310;

    /// <summary>Prefix of a HubException message when the project or session does not exist (CLI exit code 2).</summary>
    public const string NotFound = "not_found:";

    /// <summary>Prefix of a HubException message when a newer wait of the same agent on the session took over.</summary>
    public const string Superseded = "superseded:";

    public const string Status = nameof(Status);
    public const string Projects = nameof(Projects);
    public const string Sessions = nameof(Sessions);
    public const string Tail = nameof(Tail);
    public const string Wait = nameof(Wait);
}

public static class WaitReasons
{
    public const string FirstEvent = "first_event";
    public const string MinWords = "min_words";
    public const string MaxWait = "max_wait";
    public const string SessionStopped = "session_stopped";

    /// <summary>The user pressed «send to agent now»: the thought is finished and an answer is awaited.</summary>
    public const string ForcePush = "force_push";

    /// <summary>The project folder was renamed; continue with the new name and path, the cursor did not move.</summary>
    public const string ProjectMoved = "project_moved";
}

/// <summary>
/// Without a project and session the active recording is meant; with a project alone, its active recording.
/// <paramref name="SkipBacklog"/>: a session the agent has never read counts as read up to now.
/// <paramref name="WaitForStart"/>: no recording is running yet — wait for the next one instead of failing.
/// With --session it waits for the next recording into that session; without, for the next recording of the project
/// (or of any project when none is named).
/// </summary>
public sealed record WaitRequest(
    string? Project, string? Session, string Agent, int? MinWords, double? MaxWaitSeconds, bool FromStart, bool SkipBacklog = false,
    bool WaitForStart = false);

public sealed record TailRequest(string? Project, string Session, int FromSeq);

/// <summary>Where a project went: the names and the folders before and after a rename.</summary>
public sealed record ProjectMoveDto(string OldName, string NewName, string OldPath, string NewPath);

/// <summary>
/// A phrase: <paramref name="Start"/> and <paramref name="End"/> are seconds from the session's start,
/// <paramref name="StartAt"/> and <paramref name="EndAt"/> the same moments on the clock, with the time zone.
/// </summary>
public sealed record SegmentDto(int Seq, double Start, double End, DateTimeOffset? StartAt, DateTimeOffset? EndAt, string Lang, string Text);

/// <param name="SessionStarted">When the session's first recording began, with the time zone.</param>
/// <param name="Move">Set with reason project_moved: the new project name and folder to continue with.</param>
public sealed record WaitReply(
    string Project, string Session, DateTimeOffset? SessionStarted, string Reason, int FromSeq, int ToSeq, string? LangNow,
    IReadOnlyList<SegmentDto> Segments, ProjectMoveDto? Move = null);

/// <param name="SessionStarted">When the session's first recording began, with the time zone.</param>
public sealed record TailReply(string Project, string Session, DateTimeOffset? SessionStarted, IReadOnlyList<SegmentDto> Segments);

public sealed record RecordingDto(string Project, string Session, string LangNow);

public sealed record AgentDto(string Name, string Project, string Session, string State, DateTimeOffset LastSeen, int Cursor);

public sealed record StatusReply(string Version, RecordingDto? Recording, IReadOnlyList<AgentDto> Agents);

public sealed record ProjectDto(string Name, IReadOnlyList<string> Languages, string AudioRetention, int Sessions);

public sealed record SessionDto(string Name, DateTimeOffset LastRecorded, int Lines);

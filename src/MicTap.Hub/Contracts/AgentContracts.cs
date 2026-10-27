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
}

/// <summary>Without a project and session the active recording is meant; with a project alone, its active recording.</summary>
public sealed record WaitRequest(string? Project, string? Session, string Agent, int? MinWords, double? MaxWaitSeconds, bool FromStart);

public sealed record TailRequest(string? Project, string Session, int FromSeq);

public sealed record SegmentDto(int Seq, double Start, double End, string Lang, string Text);

public sealed record WaitReply(
    string Project, string Session, string Reason, int FromSeq, int ToSeq, string? LangNow, IReadOnlyList<SegmentDto> Segments);

public sealed record RecordingDto(string Project, string Session, string LangNow);

public sealed record AgentDto(string Name, string Project, string Session, string State, DateTimeOffset LastSeen, int Cursor);

public sealed record StatusReply(string Version, RecordingDto? Recording, IReadOnlyList<AgentDto> Agents);

public sealed record ProjectDto(string Name, IReadOnlyList<string> Languages, string AudioRetention, int Sessions);

public sealed record SessionDto(string Name, DateTimeOffset LastRecorded, int Lines);

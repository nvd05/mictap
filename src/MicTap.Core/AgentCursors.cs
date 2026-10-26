using System.Text.Json;

namespace MicTap.Core;

/// <summary>What the agent asked for last time, kept so its behaviour can be seen.</summary>
public sealed record AgentPolicy(int? MinWords, double? MaxWaitSeconds);

/// <summary>
/// agents\&lt;agent&gt;.json in a session: the last seq the agent received, when it was last seen, and its last policy.
/// </summary>
public sealed record AgentCursor(int Cursor, DateTimeOffset LastSeen, AgentPolicy? Policy);

public static class AgentCursors
{
    public static AgentCursor? Read(SessionInfo session, string agent)
    {
        string file = FileOf(session, agent);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentCursor>(File.ReadAllText(file), JsonFiles.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{file} is not a valid agent cursor: {ex.Message}", ex);
        }
    }

    public static void Write(SessionInfo session, string agent, AgentCursor cursor) => JsonFiles.Write(FileOf(session, agent), cursor);

    /// <summary>Agents that have a cursor in the session, by name.</summary>
    public static IReadOnlyList<(string Agent, AgentCursor Cursor)> List(SessionInfo session) =>
        !Directory.Exists(session.AgentsFolder)
            ? []
            :
            [
                .. Directory.EnumerateFiles(session.AgentsFolder, "*.json")
                    .Select(file => Path.GetFileNameWithoutExtension(file))
                    .Select(agent => (agent, Read(session, agent)))
                    .Where(entry => entry.Item2 is not null)
                    .Select(entry => (entry.agent, entry.Item2!))
                    .OrderBy(entry => entry.agent, StringComparer.OrdinalIgnoreCase),
            ];

    /// <summary>An agent name as a file name: characters Windows forbids are dropped.</summary>
    public static string FileName(string agent) => SessionFolders.Sanitize(agent) is { Length: > 0 } name ? name : "agent";

    private static string FileOf(SessionInfo session, string agent) => Path.Combine(session.AgentsFolder, FileName(agent) + ".json");
}

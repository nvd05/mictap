using System.Globalization;

namespace MicTap.Core;

/// <summary>A session folder: &lt;project&gt;\&lt;session&gt;\ with audio\, meta.json, transcript.jsonl, agents\ and notes\.</summary>
public sealed record SessionInfo(string Name, string Folder)
{
    public string AudioFolder => Path.Combine(Folder, "audio");

    public string MetaFile => Path.Combine(Folder, "meta.json");

    /// <summary>Cursors of agents that read the session.</summary>
    public string AgentsFolder => Path.Combine(Folder, "agents");

    /// <summary>For agents' own notes; mictap never writes there.</summary>
    public string NotesFolder => Path.Combine(Folder, "notes");
}

public static class SessionFolders
{
    private static readonly string[] ReservedNames =
        [ "CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" }) ];

    /// <summary>The name a new session is offered: its start time, yyyy-MM-dd_HH-mm-ss.</summary>
    public static string DefaultName(DateTimeOffset now) => now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// A name the user typed, made safe as a folder name: characters Windows forbids are dropped, spaces and dots
    /// at the ends trimmed, device names such as CON get a trailing underscore. Empty when nothing usable is left.
    /// </summary>
    public static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string kept = new([ .. name.Where(c => !invalid.Contains(c)) ]);
        kept = string.Join(' ', kept.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return ReservedNames.Contains(kept.Split('.')[0], StringComparer.OrdinalIgnoreCase) ? kept + "_" : kept;
    }

    /// <summary>Creates the session's folders if they are missing; an existing session is kept as it is.</summary>
    public static SessionInfo Open(string projectFolder, string name)
    {
        var session = new SessionInfo(name, Path.Combine(projectFolder, name));
        Directory.CreateDirectory(session.AudioFolder);
        Directory.CreateDirectory(session.AgentsFolder);
        Directory.CreateDirectory(session.NotesFolder);
        return session;
    }

    /// <summary>Sessions of a project, the most recently recorded first (by the time meta.json was last written).</summary>
    public static IReadOnlyList<SessionInfo> List(string projectFolder) =>
        !Directory.Exists(projectFolder)
            ? [ ]
            :
            [
                .. Directory.EnumerateDirectories(projectFolder)
                    .Select(folder => new SessionInfo(Path.GetFileName(folder), folder))
                    .Where(session => File.Exists(session.MetaFile))
                    .OrderByDescending(session => File.GetLastWriteTimeUtc(session.MetaFile)),
            ];
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace MicTap.Core;

/// <summary>A session folder: &lt;root&gt;\yyyy-MM-dd_HHmm\ with audio\ and meta.json inside.</summary>
public sealed record SessionInfo(string Name, string Folder)
{
    public string AudioFolder => Path.Combine(Folder, "audio");

    public string MetaFile => Path.Combine(Folder, "meta.json");
}

public static partial class SessionFolders
{
    private const string NameFormat = "yyyy-MM-dd_HHmm";

    /// <summary>
    /// Creates the folder of a session started at <paramref name="started"/> (local time). A second session in
    /// the same minute gets a suffix, yyyy-MM-dd_HHmm_2, instead of mixing its chunks into the first one.
    /// </summary>
    public static SessionInfo Create(string rootFolder, DateTimeOffset started)
    {
        string baseName = started.ToString(NameFormat, CultureInfo.InvariantCulture);
        for (int attempt = 1; ; attempt++)
        {
            string name = attempt == 1 ? baseName : $"{baseName}_{attempt}";
            var session = new SessionInfo(name, Path.Combine(rootFolder, name));
            if (!Directory.Exists(session.Folder))
            {
                Directory.CreateDirectory(session.AudioFolder);
                return session;
            }
        }
    }

    /// <summary>The most recent session folder under <paramref name="rootFolder"/>; other folders are ignored.</summary>
    public static SessionInfo? FindLatest(string rootFolder)
    {
        if (!Directory.Exists(rootFolder))
        {
            return null;
        }

        SessionInfo? latest = null;
        (DateTime Started, int Attempt) latestKey = default;
        foreach (string folder in Directory.EnumerateDirectories(rootFolder))
        {
            string name = Path.GetFileName(folder);
            if (TryParseName(name, out var key) && (latest is null || key.CompareTo(latestKey) > 0))
            {
                latest = new SessionInfo(name, folder);
                latestKey = key;
            }
        }

        return latest;
    }

    private static bool TryParseName(string name, out (DateTime Started, int Attempt) key)
    {
        Match match = NamePattern().Match(name);
        if (match.Success
            && DateTime.TryParseExact(match.Groups[1].Value, NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime started))
        {
            int attempt = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            key = (started, attempt);
            return true;
        }

        key = default;
        return false;
    }

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}_\d{4})(?:_(\d{1,4}))?$")]
    private static partial Regex NamePattern();
}

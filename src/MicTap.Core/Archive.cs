using System.Globalization;
using System.Text.Json;

namespace MicTap.Core;

public enum ArchivedKind
{
    Project,
    Session,
}

/// <summary>
/// archive.json of an archived folder: what it was, where it came from and when it was deleted.
/// <paramref name="ProjectName"/> and <paramref name="ProjectFolder"/> name the project (for a session: its project).
/// </summary>
public sealed record ArchiveInfo(
    ArchivedKind Kind, string Name, string OriginalFolder, string ProjectName, string ProjectFolder, DateTimeOffset DeletedAt)
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion => CurrentSchemaVersion;
}

/// <summary>A deleted project or session in the archive; <paramref name="Folder"/> holds it as it was, plus archive.json.</summary>
public sealed record ArchiveEntry(string Folder, ArchiveInfo Info)
{
    /// <summary>When the archive deletes it for good, or null when the archive keeps everything.</summary>
    public DateTimeOffset? ExpiresAt(TimeSpan? keep) => keep is { } days ? Info.DeletedAt + days : null;
}

/// <summary>
/// &lt;root&gt;\.archive\: deleted projects and sessions are moved there whole (text, audio, agent cursors) and can be put
/// back; <see cref="Purge"/> deletes for good what has been there longer than the user keeps it. The folder is hidden
/// and has no project.json, so project listings never see it.
/// </summary>
public static class Archive
{
    public const string FolderName = ".archive";

    public const string InfoFile = "archive.json";

    public static string FolderOf(string root) => Path.Combine(root, FolderName);

    public static ArchiveEntry ArchiveProject(string root, ProjectInfo project, DateTimeOffset now) =>
        MoveIn(root, project.Folder, new ArchiveInfo(
            ArchivedKind.Project, project.Name, Path.GetFileName(project.Folder), project.Name, Path.GetFileName(project.Folder), now));

    public static ArchiveEntry ArchiveSession(string root, ProjectInfo project, SessionInfo session, DateTimeOffset now) =>
        MoveIn(root, session.Folder, new ArchiveInfo(
            ArchivedKind.Session, session.Name, Path.GetFileName(session.Folder), project.Name, Path.GetFileName(project.Folder), now));

    /// <summary>What the archive holds, the most recently deleted first. Folders without a readable archive.json are skipped.</summary>
    public static IReadOnlyList<ArchiveEntry> List(string root)
    {
        string archive = FolderOf(root);
        if (!Directory.Exists(archive))
        {
            return [];
        }

        var entries = new List<ArchiveEntry>();
        foreach (string folder in Directory.EnumerateDirectories(archive))
        {
            string file = Path.Combine(folder, InfoFile);
            if (File.Exists(file) && JsonSerializer.Deserialize<ArchiveInfo>(File.ReadAllText(file), JsonFiles.Options) is { } info)
            {
                entries.Add(new ArchiveEntry(folder, info));
            }
        }

        return [ .. entries.OrderByDescending(e => e.Info.DeletedAt) ];
    }

    /// <summary>
    /// Puts an entry back under its old folder name, with _2, _3 … when that is taken. A session goes back into its
    /// project; when the project is gone too, the project is made again with default settings. Returns the folder.
    /// </summary>
    public static string Restore(string root, ArchiveEntry entry)
    {
        string parent = root;
        if (entry.Info.Kind == ArchivedKind.Session)
        {
            parent = Path.Combine(root, entry.Info.ProjectFolder);
            if (!File.Exists(Path.Combine(parent, Projects.FileName)))
            {
                parent = Projects.Create(root, Projects.FirstProject with { Name = entry.Info.ProjectName }).Folder;
            }
        }

        string target = FreeFolder(parent, entry.Info.OriginalFolder);
        File.Delete(Path.Combine(entry.Folder, InfoFile));
        Directory.Move(entry.Folder, target);
        return target;
    }

    /// <summary>Deletes an entry for good, not to the Recycle Bin: it has been kept as long as the user wanted.</summary>
    public static void DeleteForever(ArchiveEntry entry) => Directory.Delete(entry.Folder, recursive: true);

    /// <summary>Deletes for good the entries older than <paramref name="keep"/>; null keeps everything. Returns them.</summary>
    public static IReadOnlyList<ArchiveEntry> Purge(string root, TimeSpan? keep, DateTimeOffset now)
    {
        if (keep is null)
        {
            return [];
        }

        ArchiveEntry[] expired = [ .. List(root).Where(e => e.ExpiresAt(keep) <= now) ];
        foreach (ArchiveEntry entry in expired)
        {
            DeleteForever(entry);
        }

        return expired;
    }

    private static ArchiveEntry MoveIn(string root, string folder, ArchiveInfo info)
    {
        string archive = FolderOf(root);
        if (!Directory.Exists(archive))
        {
            Directory.CreateDirectory(archive).Attributes |= FileAttributes.Hidden;
        }

        string stamp = info.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        string name = info.Kind == ArchivedKind.Project
            ? $"{stamp} {info.OriginalFolder}"
            : $"{stamp} {info.ProjectFolder} — {info.OriginalFolder}";
        string target = FreeFolder(archive, name);
        Directory.Move(folder, target);
        JsonFiles.Write(Path.Combine(target, InfoFile), info);
        return new ArchiveEntry(target, info);
    }

    private static string FreeFolder(string parent, string name)
    {
        string folder = Path.Combine(parent, name);
        for (int copy = 2; Directory.Exists(folder); copy++)
        {
            folder = Path.Combine(parent, $"{name}_{copy}");
        }

        return folder;
    }
}

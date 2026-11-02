using System.IO;

namespace MicTap.Core;

/// <summary>Audio of one session that a new retention policy would remove.</summary>
public sealed record AudioCleanupSession(SessionInfo Session, int Files, long Bytes);

/// <summary>
/// What applying an audio retention policy to a project's old audio would remove. The plan only reads the disk;
/// <see cref="Apply"/> deletes after the user confirms. Transcripts, notes and agent cursors never go.
/// </summary>
public sealed record AudioCleanupPlan(ProjectInfo Project, AudioRetention Retention, IReadOnlyList<AudioCleanupSession> Sessions)
{
    public int Files => Sessions.Sum(session => session.Files);

    public long Bytes => Sessions.Sum(session => session.Bytes);

    public bool IsEmpty => Files == 0;

    /// <summary>Deletes the planned WAV chunks; empty audio\ folders are left for the session to use again.</summary>
    public int Apply()
    {
        int deleted = 0;
        foreach (AudioCleanupSession session in Sessions)
        {
            foreach (string file in Directory.EnumerateFiles(session.Session.AudioFolder, "*.wav"))
            {
                File.Delete(file);
                deleted++;
            }
        }

        return deleted;
    }
}

public static class AudioCleanup
{
    /// <summary>
    /// The old audio a policy would remove from <paramref name="project"/>. Keep removes nothing. For both delete
    /// policies the old chunks are not needed once their text exists; the user sees the list before anything goes.
    /// </summary>
    public static AudioCleanupPlan Plan(ProjectInfo project, AudioRetention retention) =>
        new(
            project,
            retention,
            retention == AudioRetention.Keep
                ? []
                :
                [
                    .. Directory.EnumerateDirectories(project.Folder)
                        .Select(folder => new SessionInfo(Path.GetFileName(folder), folder))
                        .Select(session =>
                        {
                            var files = Directory.Exists(session.AudioFolder)
                                ? Directory.EnumerateFiles(session.AudioFolder, "*.wav").ToList()
                                : [];
                            return new AudioCleanupSession(session, files.Count, files.Sum(file => new FileInfo(file).Length));
                        })
                        .Where(session => session.Files > 0),
                ]);
}

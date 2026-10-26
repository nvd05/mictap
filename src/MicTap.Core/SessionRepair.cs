namespace MicTap.Core;

/// <summary>Chunks of <see cref="Session"/> that were repaired or could not be recognized.</summary>
public sealed record RepairReport(SessionInfo Session, IReadOnlyList<string> Repaired, IReadOnlyList<string> Unrecognized);

/// <summary>Startup repair of chunks that a crash left open.</summary>
public static class SessionRepair
{
    /// <summary>Repairs open chunks of the latest session under <paramref name="rootFolder"/>; null if there is no session.</summary>
    public static RepairReport? RepairLatest(string rootFolder)
    {
        SessionInfo? session = SessionFolders.FindLatest(rootFolder);
        if (session is null)
        {
            return null;
        }

        var repaired = new List<string>();
        var unrecognized = new List<string>();
        if (Directory.Exists(session.AudioFolder))
        {
            foreach (string chunk in Directory.EnumerateFiles(session.AudioFolder, "*.wav").Order(StringComparer.Ordinal))
            {
                switch (WavHeader.RepairIfOpen(chunk))
                {
                    case WavRepairResult.Repaired:
                        repaired.Add(chunk);
                        break;
                    case WavRepairResult.NotRecognized:
                        unrecognized.Add(chunk);
                        break;
                }
            }
        }

        return new RepairReport(session, repaired, unrecognized);
    }
}

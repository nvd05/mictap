using System.Text.Json;

namespace MicTap.Core;

/// <summary>What one cloud answer cost: seconds of audio and rubles, when the service says it.</summary>
public sealed record CloudSpend(double Seconds, double? Rubles);

/// <summary>cloud.json of a session: phrases sent to the cloud, their seconds of audio and what they cost in rubles.</summary>
public sealed record CloudUsage(int Requests, double Seconds, double Rubles)
{
    public const int CurrentSchemaVersion = 1;

    public static CloudUsage None { get; } = new(0, 0, 0);

    public int SchemaVersion => CurrentSchemaVersion;

    public CloudUsage Add(CloudUsage other) => new(Requests + other.Requests, Seconds + other.Seconds, Rubles + other.Rubles);
}

/// <summary>Keeps the cloud spending of each session in its cloud.json, so a project's total can be summed up.</summary>
public static class CloudUsageLog
{
    public const string FileName = "cloud.json";

    /// <summary>Adds one answer to the session's total and returns the new total. One writer at a time: the pipeline.</summary>
    public static CloudUsage Add(SessionInfo session, CloudSpend spend)
    {
        CloudUsage total = Read(session).Add(new CloudUsage(1, spend.Seconds, spend.Rubles ?? 0));
        JsonFiles.Write(FileOf(session), total);
        return total;
    }

    public static CloudUsage Read(SessionInfo session)
    {
        string file = FileOf(session);
        if (!File.Exists(file))
        {
            return CloudUsage.None;
        }

        try
        {
            return JsonSerializer.Deserialize<CloudUsage>(File.ReadAllText(file), JsonFiles.Options) ?? CloudUsage.None;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{file} is not a valid cloud usage: {ex.Message}", ex);
        }
    }

    /// <summary>Everything the sessions of a project spent in the cloud.</summary>
    public static CloudUsage Total(string projectFolder) =>
        SessionFolders.List(projectFolder).Aggregate(CloudUsage.None, (total, session) => total.Add(Read(session)));

    private static string FileOf(SessionInfo session) => Path.Combine(session.Folder, FileName);
}

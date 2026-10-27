using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicTap.Core;

/// <summary>
/// What one cloud answer cost: seconds of audio and rubles, when the service says it; the tokens it counted, when it
/// counts them; and the words of the text it gave back.
/// </summary>
public sealed record CloudSpend(double Seconds, double? Rubles, int? InputTokens = null, int? OutputTokens = null, int Words = 0);

/// <summary>What one model did in a session: answers, seconds of audio, rubles, tokens in and out, words of text back.</summary>
public sealed record ModelUsage(int Requests, double Seconds, double Rubles, long InputTokens, long OutputTokens, long Words)
{
    public static ModelUsage None { get; } = new(0, 0, 0, 0, 0, 0);

    public ModelUsage Add(CloudSpend spend) => new(
        Requests + 1, Seconds + spend.Seconds, Rubles + (spend.Rubles ?? 0),
        InputTokens + (spend.InputTokens ?? 0), OutputTokens + (spend.OutputTokens ?? 0), Words + spend.Words);

    public ModelUsage Add(ModelUsage other) => new(
        Requests + other.Requests, Seconds + other.Seconds, Rubles + other.Rubles,
        InputTokens + other.InputTokens, OutputTokens + other.OutputTokens, Words + other.Words);
}

/// <summary>
/// cloud.json of a session: everything sent to the cloud and what it cost, in total and per model. Schema 1 files
/// have the totals only: what they counted shows as spent without a model.
/// </summary>
public sealed record CloudUsage(int Requests, double Seconds, double Rubles, IReadOnlyDictionary<string, ModelUsage>? Models = null)
{
    public const int CurrentSchemaVersion = 2;

    public static CloudUsage None { get; } = new(0, 0, 0);

    [JsonPropertyOrder(-1)]
    public int SchemaVersion => CurrentSchemaVersion;

    /// <summary>Per model; never null.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ModelUsage> ByModel => Models ?? new Dictionary<string, ModelUsage>();

    /// <summary>Rubles counted before cloud.json had models (schema 1).</summary>
    [JsonIgnore]
    public double RublesWithoutModel => Math.Max(0, Rubles - ByModel.Values.Sum(m => m.Rubles));

    public ModelUsage Of(string model) => ByModel.GetValueOrDefault(model) ?? ModelUsage.None;

    public CloudUsage Add(string model, CloudSpend spend)
    {
        var models = new Dictionary<string, ModelUsage>(ByModel) { [ model ] = Of(model).Add(spend) };
        return new CloudUsage(Requests + 1, Seconds + spend.Seconds, Rubles + (spend.Rubles ?? 0), models);
    }

    public CloudUsage Add(CloudUsage other)
    {
        var models = new Dictionary<string, ModelUsage>(ByModel);
        foreach ((string model, ModelUsage usage) in other.ByModel)
        {
            models[ model ] = (models.GetValueOrDefault(model) ?? ModelUsage.None).Add(usage);
        }

        return new CloudUsage(Requests + other.Requests, Seconds + other.Seconds, Rubles + other.Rubles, models);
    }
}

/// <summary>Keeps the cloud spending of each session in its cloud.json, so a project's total can be summed up.</summary>
public static class CloudUsageLog
{
    public const string FileName = "cloud.json";

    // Answers come in parallel (a comparison asks many models at once): read, add and write one at a time.
    private static readonly Lock Gate = new();

    /// <summary>Adds one answer of <paramref name="model"/> to the session's totals and returns them. Safe from several threads.</summary>
    public static CloudUsage Add(SessionInfo session, string model, CloudSpend spend)
    {
        lock (Gate)
        {
            CloudUsage total = Read(session).Add(model, spend);
            JsonFiles.Write(FileOf(session), total);
            return total;
        }
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

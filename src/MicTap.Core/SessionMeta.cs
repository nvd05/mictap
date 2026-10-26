using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicTap.Core;

/// <summary>Audio format as stored in meta.json.</summary>
public sealed record MetaFormat(string Encoding, int SampleRate, int Channels, int BitsPerSample)
{
    public static MetaFormat From(PcmFormat format) =>
        new($"pcm_s{format.BitsPerSample}le", format.SampleRate, format.Channels, format.BitsPerSample);
}

/// <summary>
/// A recording into the session: wall-clock start, offset in seconds of session audio, and its length in seconds once
/// it stopped (missing after a crash).
/// </summary>
public sealed record SessionStart(
    DateTimeOffset Time, double Offset, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Duration = null);

/// <summary>From <paramref name="At"/> seconds of session audio on, speech is in <paramref name="Lang"/>.</summary>
public sealed record LanguageMark(double At, string Lang);

/// <summary>
/// Contents of a session's meta.json — a format agents rely on; change it only with a new schema version.
/// The first language mark is at 0 and holds the language the session started with.
/// </summary>
public sealed record SessionMeta(
    IReadOnlyList<SessionStart> Starts,
    string Device,
    MetaFormat Format,
    int ChunkSeconds,
    string AppVersion,
    IReadOnlyList<LanguageMark> LanguageMarks)
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion => CurrentSchemaVersion;

    public void WriteTo(SessionInfo session) => JsonFiles.Write(session.MetaFile, this);

    /// <summary>
    /// Reads meta.json of schema 2, or of schema 1 (before v0.3: one start, no language marks; the session is then
    /// taken as spoken in <paramref name="languageOfSchema1"/>).
    /// </summary>
    public static SessionMeta Read(SessionInfo session, string languageOfSchema1)
    {
        string json = File.ReadAllText(session.MetaFile);
        int version;
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            version = document.RootElement.TryGetProperty("schemaVersion", out JsonElement v) ? v.GetInt32() : 0;
        }

        switch (version)
        {
            case CurrentSchemaVersion:
                return JsonSerializer.Deserialize<SessionMeta>(json, JsonFiles.Options)
                    ?? throw new InvalidDataException($"{session.MetaFile} contains null.");
            case 1:
                MetaOfSchema1 old = JsonSerializer.Deserialize<MetaOfSchema1>(json, JsonFiles.Options)
                    ?? throw new InvalidDataException($"{session.MetaFile} contains null.");
                return new SessionMeta(
                    [new SessionStart(old.Started, 0)], old.Device, old.Format, old.ChunkSeconds, old.AppVersion,
                    [new LanguageMark(0, languageOfSchema1)]);
            default:
                throw new InvalidDataException($"{session.MetaFile}: unknown schemaVersion {version}.");
        }
    }

    private sealed record MetaOfSchema1(DateTimeOffset Started, string Device, MetaFormat Format, int ChunkSeconds, string AppVersion);
}

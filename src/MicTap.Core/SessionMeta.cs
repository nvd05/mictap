using System.Text.Json.Serialization;

namespace MicTap.Core;

/// <summary>Audio format as stored in meta.json.</summary>
public sealed record MetaFormat(string Encoding, int SampleRate, int Channels, int BitsPerSample)
{
    public static MetaFormat From(PcmFormat format) =>
        new($"pcm_s{format.BitsPerSample}le", format.SampleRate, format.Channels, format.BitsPerSample);
}

/// <summary>Contents of a session's meta.json — a format agents rely on; change it only with a new schema version.</summary>
public sealed record SessionMeta(DateTimeOffset Started, string Device, MetaFormat Format, int ChunkSeconds, string AppVersion)
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion => CurrentSchemaVersion;

    public void WriteTo(SessionInfo session) => JsonFiles.Write(session.MetaFile, this);
}

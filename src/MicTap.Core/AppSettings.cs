namespace MicTap.Core;

/// <summary>User settings, stored as settings.json.</summary>
public sealed record AppSettings
{
    public const int DefaultChunkSeconds = 60;

    /// <summary>Upper bound that keeps a chunk far below the 4 GB WAV limit.</summary>
    public const int MaxChunkSeconds = 3600;

    /// <summary>Folder that receives session folders; null until the user picks one.</summary>
    public string? RootFolder { get; init; }

    /// <summary>Microphone name as reported by WinMM; null until the user picks one.</summary>
    public string? DeviceName { get; init; }

    public int ChunkSeconds { get; init; } = DefaultChunkSeconds;
}

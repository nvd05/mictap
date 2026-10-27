namespace MicTap.Core;

/// <summary>User settings, stored as settings.json.</summary>
public sealed record AppSettings
{
    public const int DefaultChunkSeconds = 60;

    /// <summary>Upper bound that keeps a chunk far below the 4 GB WAV limit.</summary>
    public const int MaxChunkSeconds = 3600;

    public const int DefaultHubPort = 47310;

    public const int DefaultLiveDraftSeconds = 3;

    public const int MaxLiveDraftSeconds = 60;

    public const int DefaultArchiveDays = 30;

    public const int MaxArchiveDays = 3650;

    /// <summary>Folder of projects; null means <see cref="Projects.DefaultRoot"/>.</summary>
    public string? RootFolder { get; init; }

    /// <summary>Microphone name as reported by WinMM; null until the user picks one.</summary>
    public string? DeviceName { get; init; }

    public int ChunkSeconds { get; init; } = DefaultChunkSeconds;

    public HotkeySettings Hotkeys { get; init; } = new();

    /// <summary>Port of the agents hub on 127.0.0.1; the mictap CLI reads it from here.</summary>
    public int HubPort { get; init; } = DefaultHubPort;

    /// <summary>
    /// How often the draft of what is being said is recognized, in seconds; 0 turns the draft off. The draft is the
    /// graphics card's steady load: finished phrases are recognized once whatever this is.
    /// </summary>
    public int LiveDraftSeconds { get; init; } = DefaultLiveDraftSeconds;

    /// <summary>Days a deleted project or session stays in the archive before it is deleted for good; 0 keeps it forever.</summary>
    public int ArchiveDays { get; init; } = DefaultArchiveDays;
}

/// <summary>Global hotkeys, in the form "Ctrl+Alt+L"; see <see cref="Hotkey.TryParse"/>.</summary>
public sealed record HotkeySettings
{
    public string ToggleLanguage { get; init; } = "Ctrl+Alt+L";

    public string ToggleRecording { get; init; } = "Ctrl+Alt+R";

    /// <summary>Sends what has been said to the waiting agents at once, see <see cref="TranscriptionPipeline.Push"/>.</summary>
    public string ForcePush { get; init; } = "Ctrl+Alt+Enter";
}

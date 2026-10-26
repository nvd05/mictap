namespace MicTap.Core;

/// <summary>User settings, stored as settings.json.</summary>
public sealed record AppSettings
{
    public const int DefaultChunkSeconds = 60;

    /// <summary>Upper bound that keeps a chunk far below the 4 GB WAV limit.</summary>
    public const int MaxChunkSeconds = 3600;

    public const string DefaultLanguage = "ru";

    /// <summary>Languages offered for recognition, as Whisper language codes.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["ru", "ja"];

    /// <summary>Folder that receives session folders; null until the user picks one.</summary>
    public string? RootFolder { get; init; }

    /// <summary>Microphone name as reported by WinMM; null until the user picks one.</summary>
    public string? DeviceName { get; init; }

    public int ChunkSeconds { get; init; } = DefaultChunkSeconds;

    /// <summary>Speech language for recognition: one of <see cref="Languages"/>.</summary>
    public string Language { get; init; } = DefaultLanguage;

    public HotkeySettings Hotkeys { get; init; } = new();
}

/// <summary>Global hotkeys, in the form "Ctrl+Alt+L"; see <see cref="Hotkey.TryParse"/>.</summary>
public sealed record HotkeySettings
{
    public string ToggleLanguage { get; init; } = "Ctrl+Alt+L";

    public string ToggleRecording { get; init; } = "Ctrl+Alt+R";
}

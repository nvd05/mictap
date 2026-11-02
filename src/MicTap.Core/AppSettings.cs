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

    /// <summary>The graphics card (Whisper on this computer) among <see cref="LikedModels"/>.</summary>
    public const string LocalModel = "local";

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

    /// <summary>
    /// Projects (their folder names under the root) the window does not list until asked to: for eyes over the shoulder.
    /// Agents still see them.
    /// </summary>
    public IReadOnlyList<string> HiddenProjects { get; init; } = [];

    /// <summary>Projects (their folder names under the root) listed first.</summary>
    public IReadOnlyList<string> PinnedProjects { get; init; } = [];

    /// <summary>
    /// Recognition engines the user liked, for every project: Router AI model ids and <see cref="LocalModel"/> for the
    /// graphics card. They come first in the engine lists.
    /// </summary>
    public IReadOnlyList<string> LikedModels { get; init; } = [];

    /// <summary>Languages shown first in the language lists; Russian and Japanese by default.</summary>
    public IReadOnlyList<string> FavoriteLanguages { get; init; } = [ "ru", "ja" ];

    /// <summary>What a new project starts with; existing project.json files are never rewritten from these.</summary>
    public NewProjectDefaults NewProjectDefaults { get; init; } = new();

    /// <summary>Whether the user liked <paramref name="model"/> (null — the graphics card).</summary>
    public bool Likes(string? model) => LikedModels.Contains(model ?? LocalModel);

    /// <summary>These settings with <paramref name="model"/> (null — the graphics card) liked or not.</summary>
    public AppSettings WithLike(string? model, bool liked)
    {
        string id = model ?? LocalModel;
        IEnumerable<string> others = LikedModels.Where(m => m != id);
        return this with { LikedModels = [ .. liked ? others.Append(id) : others ] };
    }

    /// <summary>Whether a language is among the favorites.</summary>
    public bool LikesLanguage(string code) => FavoriteLanguages.Contains(code, StringComparer.OrdinalIgnoreCase);

    /// <summary>These settings with a language liked or not.</summary>
    public AppSettings WithLanguageLike(string code, bool liked)
    {
        IEnumerable<string> others = FavoriteLanguages.Where(language => !string.Equals(language, code, StringComparison.OrdinalIgnoreCase));
        return this with { FavoriteLanguages = [ .. liked ? others.Append(code) : others ] };
    }

    // The lists compare by their items: a record compares them by reference.
    public bool Equals(AppSettings? other) =>
        other is not null
        && RootFolder == other.RootFolder
        && DeviceName == other.DeviceName
        && ChunkSeconds == other.ChunkSeconds
        && Hotkeys == other.Hotkeys
        && HubPort == other.HubPort
        && LiveDraftSeconds == other.LiveDraftSeconds
        && ArchiveDays == other.ArchiveDays
        && HiddenProjects.SequenceEqual(other.HiddenProjects)
        && PinnedProjects.SequenceEqual(other.PinnedProjects)
        && LikedModels.SequenceEqual(other.LikedModels)
        && FavoriteLanguages.SequenceEqual(other.FavoriteLanguages)
        && NewProjectDefaults.Equals(other.NewProjectDefaults);

    public override int GetHashCode() =>
        HashCode.Combine(RootFolder, DeviceName, ChunkSeconds, Hotkeys, HubPort, LiveDraftSeconds, ArchiveDays, HiddenProjects.Count);
}

/// <summary>
/// Values a new project is created with. They come from the settings page and never rewrite existing projects.
/// The model defaults to the remote model the user's projects already use and marks as liked; the settings expose it
/// explicitly, so a wrong guess is one click away from being fixed.
/// </summary>
public sealed record NewProjectDefaults
{
    public IReadOnlyList<string> Languages { get; init; } = [ "ru" ];

    public AudioRetention AudioRetention { get; init; } = AudioRetention.DeleteAfterTranscript;

    /// <summary>The project's own draft frequency; 0 is off, null follows the app setting.</summary>
    public int? LiveDraftSeconds { get; init; } = 0;

    public double PhrasePauseSeconds { get; init; } = 3;

    public TranscriptionEngine Engine { get; init; } = TranscriptionEngine.RouterAi;

    public string CloudModel { get; init; } = "mistralai/voxtral-small-transcribe-3-0";

    public ProjectSettings ForProject(string name) =>
        new(name, Languages, AudioRetention, PhrasePauseSeconds, Engine, CloudModel, LiveDraftSeconds);

    public bool Equals(NewProjectDefaults? other) =>
        other is not null
        && Languages.SequenceEqual(other.Languages)
        && AudioRetention == other.AudioRetention
        && LiveDraftSeconds == other.LiveDraftSeconds
        && PhrasePauseSeconds == other.PhrasePauseSeconds
        && Engine == other.Engine
        && CloudModel == other.CloudModel;

    public override int GetHashCode() =>
        HashCode.Combine(AudioRetention, LiveDraftSeconds, PhrasePauseSeconds, Engine, CloudModel, Languages.Count);
}

/// <summary>Global hotkeys, in the form "Ctrl+Alt+L"; see <see cref="Hotkey.TryParse"/>.</summary>
public sealed record HotkeySettings
{
    public string ToggleLanguage { get; init; } = "Ctrl+Alt+L";

    public string ToggleRecording { get; init; } = "Ctrl+Alt+R";

    /// <summary>Sends what has been said to the waiting agents at once, see <see cref="TranscriptionPipeline.Push"/>.</summary>
    public string ForcePush { get; init; } = "Ctrl+Alt+Enter";
}

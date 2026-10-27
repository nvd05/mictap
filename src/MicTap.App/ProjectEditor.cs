using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;

namespace MicTap.App;

public sealed record RetentionOption(AudioRetention Value, string Name)
{
    /// <summary>What screen readers and UI Automation call the item.</summary>
    public override string ToString() => Name;
}

public sealed record EngineOption(TranscriptionEngine Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>The project dialog: a new project, or the settings of an existing one (its name stays).</summary>
public sealed partial class ProjectEditor : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _languagesText = "ru, ja";

    [ObservableProperty]
    private AudioRetention _retention = AudioRetention.Keep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhrasePauseText))]
    private double _phrasePauseSeconds = ProjectSettings.DefaultPhrasePauseSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloud))]
    private TranscriptionEngine _engine = TranscriptionEngine.Local;

    [ObservableProperty]
    private string _cloudModel = RouterAiTranscriber.DefaultModel;

    [ObservableProperty]
    private string _error = "";

    // Liked models are set in the engine box, not here: an edit keeps them.
    private IReadOnlyList<string>? _likedModels;

    private ProjectEditor(bool isNew) => IsNew = isNew;

    public static IReadOnlyList<RetentionOption> RetentionOptions { get; } =
    [
        new(AudioRetention.Keep, "Хранить"),
        new(AudioRetention.DeleteAfterTranscript, "Удалять после распознавания"),
        new(AudioRetention.None, "Не сохранять (распознать заново будет нельзя)"),
    ];

    public static IReadOnlyList<EngineOption> EngineOptions { get; } =
    [
        new(TranscriptionEngine.Local, "На этом компьютере (Whisper)"),
        new(TranscriptionEngine.RouterAi, "Router AI — облако, платно"),
    ];

    public bool IsNew { get; }

    public bool IsCloud => Engine == TranscriptionEngine.RouterAi;

    public string Title => IsNew ? "Новый проект" : "Настройки проекта";

    public string PhrasePauseText => $"{PhrasePauseSeconds:0.0} с";

    public static ProjectEditor ForNew() => new(isNew: true);

    public static ProjectEditor For(ProjectInfo project) => new(isNew: false)
    {
        Name = project.Name,
        LanguagesText = string.Join(", ", project.Settings.Languages),
        Retention = project.Settings.AudioRetention,
        PhrasePauseSeconds = project.Settings.PhrasePauseSeconds,
        Engine = project.Settings.Engine,
        CloudModel = project.Settings.CloudModel,
        _likedModels = project.Settings.LikedModels,
    };

    /// <summary>The settings typed in; on a mistake <see cref="Error"/> says what is wrong and the result is null.</summary>
    public ProjectSettings? Build()
    {
        var settings = new ProjectSettings(
            Name.Trim(), Projects.ParseLanguages(LanguagesText), Retention, Math.Round(PhrasePauseSeconds, 1), Engine, CloudModel.Trim(),
            _likedModels);
        try
        {
            Projects.Validate(settings);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return null;
        }

        Error = "";
        return settings;
    }
}

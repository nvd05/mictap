using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicTap.Core;

namespace MicTap.App;

public sealed record RetentionOption(AudioRetention Value, string Name)
{
    /// <summary>What screen readers and UI Automation call the item.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The project dialog: a new project, or the settings of an existing one (its name stays). Who recognizes the phrases is
/// picked from the same list as the engine box above the session's text: the graphics card or a Router AI model, with
/// search and the liked ones first.
/// </summary>
public sealed partial class ProjectEditor : ObservableObject, IEngineSearch
{
    private readonly IReadOnlyList<CloudModelInfo> _catalog;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string?, bool> _setLike;

    // The Router AI model of the project, kept when the graphics card is picked, so that switching back finds it.
    private readonly string _cloudModel;

    // The engine picked last; a search that ends without a pick shows it again.
    private string? _picked;

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
    private EngineChoice? _selectedEngine;

    [ObservableProperty]
    private string _error = "";

    private ProjectEditor(
        bool isNew, string? engine, string cloudModel, IReadOnlyList<CloudModelInfo> catalog, Func<AppSettings> settings,
        Action<string?, bool> setLike)
    {
        IsNew = isNew;
        _catalog = catalog;
        _settings = settings;
        _setLike = setLike;
        _cloudModel = cloudModel;
        _picked = engine;
        ShowPicked();
    }

    public static IReadOnlyList<RetentionOption> RetentionOptions { get; } =
    [
        new(AudioRetention.Keep, "Хранить"),
        new(AudioRetention.DeleteAfterTranscript, "Удалять после распознавания"),
        new(AudioRetention.None, "Не сохранять (распознать заново будет нельзя)"),
    ];

    public bool IsNew { get; }

    public string Title => IsNew ? "Новый проект" : "Настройки проекта";

    public string PhrasePauseText => $"{PhrasePauseSeconds:0.0} с";

    public EngineList Engines { get; } = new();

    public bool IsUpdatingEngines => Engines.IsRebuilding;

    /// <param name="setLike">Likes a model for every project (settings.json and the engine box of the window).</param>
    public static ProjectEditor ForNew(IReadOnlyList<CloudModelInfo> catalog, Func<AppSettings> settings, Action<string?, bool> setLike) =>
        new(isNew: true, engine: null, RouterAiTranscriber.DefaultModel, catalog, settings, setLike);

    /// <inheritdoc cref="ForNew"/>
    public static ProjectEditor For(ProjectInfo project, IReadOnlyList<CloudModelInfo> catalog, Func<AppSettings> settings, Action<string?, bool> setLike) =>
        new(isNew: false, project.Settings.CloudModelInUse, project.Settings.CloudModel, catalog, settings, setLike)
        {
            Name = project.Name,
            LanguagesText = string.Join(", ", project.Settings.Languages),
            Retention = project.Settings.AudioRetention,
            PhrasePauseSeconds = project.Settings.PhrasePauseSeconds,
        };

    public void EngineSearchClosed()
    {
        if (Engines.IsSearching || SelectedEngine is null)
        {
            ShowPicked();
        }
    }

    /// <summary>The settings typed in; on a mistake <see cref="Error"/> says what is wrong and the result is null.</summary>
    public ProjectSettings? Build()
    {
        var settings = new ProjectSettings(
            Name.Trim(), Projects.ParseLanguages(LanguagesText), Retention, Math.Round(PhrasePauseSeconds, 1),
            _picked is null ? TranscriptionEngine.Local : TranscriptionEngine.RouterAi, _picked ?? _cloudModel);
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

    partial void OnSelectedEngineChanged(EngineChoice? value)
    {
        if (value is not null && !Engines.IsRebuilding)
        {
            _picked = value.Model;
        }
    }

    /// <summary>The heart of a model in the list: liked for every project, as in the window's engine box.</summary>
    [RelayCommand]
    private void ToggleLike(EngineChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        choice.IsLiked = !choice.IsLiked;
        _setLike(choice.Model, choice.IsLiked);
    }

    private void ShowPicked()
    {
        Engines.Rebuild(_catalog, _settings(), [ _picked, _cloudModel ]);
        SelectedEngine = Engines.Find(_picked);
    }
}

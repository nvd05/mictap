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
/// The project dialog: a new project, or the settings and folder name of an existing one. Who recognizes the phrases is
/// picked from the same list as the engine box above the session's text: the graphics card or a Router AI model, with
/// search and the liked ones first.
/// </summary>
public sealed partial class ProjectEditor : ObservableObject, IEngineSearch
{
    private IReadOnlyList<CloudModelInfo> _catalog;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string?, bool> _setLike;

    // The Router AI model of the project, kept when the graphics card is picked, so that switching back finds it.
    private readonly string _cloudModel;

    // The engine picked last; a search that ends without a pick shows it again.
    private string? _picked;

    [ObservableProperty]
    private string _name = "";

    public LanguageSelection Languages { get; private set; } = null!;

    [ObservableProperty]
    private int _liveDraftSeconds;

    public bool IsDefaults { get; init; }

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
        Action<string?, bool> setLike, Action<IReadOnlyList<string>> setFavorites)
    {
        IsNew = isNew;
        _catalog = catalog;
        _settings = settings;
        _setLike = setLike;
        _cloudModel = cloudModel;
        _picked = engine;
        Languages = new LanguageSelection([ "ru" ], settings().FavoriteLanguages, setFavorites);
        ShowPicked();
    }

    public static IReadOnlyList<RetentionOption> RetentionOptions { get; } =
    [
        new(AudioRetention.Keep, "Хранить"),
        new(AudioRetention.DeleteAfterTranscript, "Удалять после распознавания"),
        new(AudioRetention.None, "Не сохранять (распознать заново будет нельзя)"),
    ];

    public bool IsNew { get; }

    public string Title => IsDefaults ? "Новые проекты" : IsNew ? "Новый проект" : "Настройки проекта";

    public string PhrasePauseText => $"{PhrasePauseSeconds:0.0} с";

    public EngineList Engines { get; } = new();

    public bool IsUpdatingEngines => Engines.IsRebuilding;

    /// <param name="setLike">Likes a model for every project (settings.json and the engine box of the window).</param>
    public static ProjectEditor ForNew(IReadOnlyList<CloudModelInfo> catalog, Func<AppSettings> settings, Action<string?, bool> setLike,
        Action<IReadOnlyList<string>> setFavorites) => For(new ProjectInfo("", settings().NewProjectDefaults.ForProject("")), catalog, settings, setLike, setFavorites, isNew: true);

    /// <inheritdoc cref="ForNew"/>
    public static ProjectEditor For(ProjectInfo project, IReadOnlyList<CloudModelInfo> catalog, Func<AppSettings> settings, Action<string?, bool> setLike,
        Action<IReadOnlyList<string>> setFavorites, bool isNew = false, bool isDefaults = false) =>
        new(isNew, project.Settings.CloudModelInUse, project.Settings.CloudModel, catalog, settings, setLike, setFavorites)
        {
            Name = project.Name,
            Languages = new LanguageSelection(project.Settings.Languages, settings().FavoriteLanguages, setFavorites),
            Retention = project.Settings.AudioRetention,
            PhrasePauseSeconds = project.Settings.PhrasePauseSeconds,
            LiveDraftSeconds = project.Settings.LiveDraftSeconds ?? settings().LiveDraftSeconds,
            IsDefaults = isDefaults,
        };

    public void UpdateCatalog(IReadOnlyList<CloudModelInfo> catalog)
    {
        _catalog = catalog;
        ShowPicked();
    }

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
            Name.Trim(), Languages.SelectedCodes, Retention, Math.Round(PhrasePauseSeconds, 1),
            _picked is null ? TranscriptionEngine.Local : TranscriptionEngine.RouterAi, _picked ?? _cloudModel, LiveDraftSeconds);
        try
        {
            Projects.Validate(settings);
            if (settings.Languages.Any(code => !LanguageCatalog.Supports(code))) throw new ArgumentException("Выберите поддерживаемый язык из списка.");
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

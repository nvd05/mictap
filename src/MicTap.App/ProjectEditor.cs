using System.Collections.ObjectModel;
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
/// The project dialog: a new project, or the settings of an existing one. Changes made during a recording never stop
/// it: the engine and the draft apply at once, the phrase pause, languages and audio retention from the next recording.
/// </summary>
public sealed partial class ProjectEditor : ObservableObject, IEngineSearch, ILanguageSearch
{
    private readonly IReadOnlyList<CloudModelInfo> _catalog;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string?, bool> _setLike;
    private readonly Action<string, bool> _setLanguageLike;

    // The Router AI model of the project, kept when the graphics card is picked, so that switching back finds it.
    private readonly string _cloudModel;

    // The engine picked last; a search that ends without a pick shows it again.
    private string? _picked;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private AudioRetention _retention = AudioRetention.DeleteAfterTranscript;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhrasePauseText))]
    private double _phrasePauseSeconds = ProjectSettings.DefaultPhrasePauseSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftText))]
    private int _draftSeconds;

    [ObservableProperty]
    private EngineChoice? _selectedEngine;

    [ObservableProperty]
    private LanguageChoice? _selectedLanguage;

    [ObservableProperty]
    private string _error = "";

    private ProjectEditor(
        bool isNew, string? engine, string cloudModel, IReadOnlyList<CloudModelInfo> catalog,
        Func<AppSettings> settings, Action<string?, bool> setLike, Action<string, bool> setLanguageLike)
    {
        IsNew = isNew;
        _cloudModel = cloudModel;
        _catalog = catalog;
        _settings = settings;
        _setLike = setLike;
        _setLanguageLike = setLanguageLike;
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

    public string DraftText => DraftSeconds == 0 ? "выключен" : $"каждые {DraftSeconds} с";

    public string ApplyNote =>
        "Запись не останавливается. Распознавание и черновик действуют сразу, и на идущей записи. "
        + "Пауза фразы, языки и хранение звука — со следующей записи.";

    public ObservableCollection<LanguageInfo> SelectedLanguages { get; } = [];

    public EngineList Engines { get; } = new();

    public LanguageList Languages { get; } = new();

    public bool IsUpdatingEngines => Engines.IsRebuilding;

    public bool IsUpdatingLanguages => Languages.IsRebuilding;

    /// <param name="setLike">Likes a model for every project (settings.json and the engine box of the window).</param>
    public static ProjectEditor ForNew(
        IReadOnlyList<CloudModelInfo> catalog, Func<AppSettings> settings, Action<string?, bool> setLike, Action<string, bool> setLanguageLike)
    {
        NewProjectDefaults defaults = settings().NewProjectDefaults;
        var editor = new ProjectEditor(
            isNew: true,
            engine: defaults.Engine == TranscriptionEngine.RouterAi ? defaults.CloudModel : null,
            cloudModel: defaults.CloudModel,
            catalog,
            settings,
            setLike,
            setLanguageLike)
        {
            Retention = defaults.AudioRetention,
            PhrasePauseSeconds = defaults.PhrasePauseSeconds,
            DraftSeconds = defaults.LiveDraftSeconds ?? settings().LiveDraftSeconds,
        };
        foreach (string language in defaults.Languages)
        {
            editor.AddLanguage(language);
        }

        return editor;
    }

    /// <inheritdoc cref="ForNew"/>
    public static ProjectEditor For(
        ProjectInfo project, IReadOnlyList<CloudModelInfo> catalog, Func<AppSettings> settings, Action<string?, bool> setLike,
        Action<string, bool> setLanguageLike)
    {
        var editor = new ProjectEditor(
            isNew: false,
            engine: project.Settings.CloudModelInUse,
            cloudModel: project.Settings.CloudModel,
            catalog,
            settings,
            setLike,
            setLanguageLike)
        {
            Name = project.Name,
            Retention = project.Settings.AudioRetention,
            PhrasePauseSeconds = project.Settings.PhrasePauseSeconds,
            DraftSeconds = project.Settings.LiveDraftSeconds ?? settings().LiveDraftSeconds,
        };
        foreach (string language in project.Settings.Languages)
        {
            editor.AddLanguage(language);
        }

        return editor;
    }

    public void EngineSearchClosed()
    {
        if (Engines.IsSearching || SelectedEngine is null)
        {
            ShowPicked();
        }
    }

    public void LanguageSearchClosed()
    {
        Languages.Rebuild(_settings(), SelectedLanguages.Select(language => language.Code));
        SelectedLanguage = null;
    }

    /// <summary>The settings typed in; on a mistake <see cref="Error"/> says what is wrong and the result is null.</summary>
    public ProjectSettings? Build()
    {
        string[] codes = [ .. SelectedLanguages.Select(language => language.Code) ];
        if (codes.FirstOrDefault(code => !LanguageCatalog.Supports(code)) is { } bad)
        {
            Error = $"«{bad}» — не поддерживаемый язык.";
            return null;
        }

        var settings = new ProjectSettings(
            Name.Trim(), codes, Retention, Math.Round(PhrasePauseSeconds, 1),
            _picked is null ? TranscriptionEngine.Local : TranscriptionEngine.RouterAi, _picked ?? _cloudModel, DraftSeconds);
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

    partial void OnSelectedLanguageChanged(LanguageChoice? value)
    {
        if (value is not null && !Languages.IsRebuilding)
        {
            AddLanguage(value.Code);
            SelectedLanguage = null;
        }
    }

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

    [RelayCommand]
    private void ToggleLanguageLike(LanguageChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        choice.IsFavorite = !choice.IsFavorite;
        Languages.SetFavorite(choice.Code, choice.IsFavorite);
        _setLanguageLike(choice.Code, choice.IsFavorite);
    }

    [RelayCommand]
    private void RemoveLanguage(LanguageInfo? language)
    {
        if (language is not null && SelectedLanguages.Count > 1 && SelectedLanguages.Remove(language))
        {
            Languages.SetPicked(language.Code, false);
        }
    }

    [RelayCommand]
    private void MakeDefaultLanguage(LanguageInfo? language)
    {
        if (language is not null && SelectedLanguages.IndexOf(language) > 0)
        {
            SelectedLanguages.Remove(language);
            SelectedLanguages.Insert(0, language);
        }
    }

    private void AddLanguage(string code)
    {
        if (LanguageCatalog.Find(code) is not { } language || SelectedLanguages.Any(selected => selected.Code == language.Code))
        {
            return;
        }

        SelectedLanguages.Add(language);
        Languages.SetPicked(code, true);
    }

    private void ShowPicked()
    {
        Engines.Rebuild(_catalog, _settings(), [ _picked, _cloudModel ]);
        SelectedEngine = Engines.Find(_picked);
        Languages.Rebuild(_settings(), SelectedLanguages.Select(language => language.Code));
    }
}

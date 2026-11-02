using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicTap.Core;

namespace MicTap.App;

/// <summary>
/// The «Новые проекты» settings section: what every project created from now on starts with. Existing projects are not
/// touched. The model is picked from the same list as the project's own, favorites first, and is an explicit choice.
/// </summary>
public sealed partial class NewProjectDefaultsEditor : ObservableObject, IEngineSearch, ILanguageSearch
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Action<string?, bool> _setModelLike;
    private readonly Action<string, bool> _setLanguageLike;
    private IReadOnlyList<CloudModelInfo> _catalog;
    private bool _loading;

    [ObservableProperty]
    private AudioRetention _retention = AudioRetention.DeleteAfterTranscript;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhrasePauseText))]
    private double _phrasePauseSeconds = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftText))]
    private int _draftSeconds;

    [ObservableProperty]
    private EngineChoice? _selectedEngine;

    [ObservableProperty]
    private LanguageChoice? _selectedLanguage;

    public NewProjectDefaultsEditor(
        IReadOnlyList<CloudModelInfo> catalog, Func<AppSettings> settings, Action<AppSettings> save,
        Action<string?, bool> setModelLike, Action<string, bool> setLanguageLike)
    {
        _catalog = catalog;
        _settings = settings;
        _save = save;
        _setModelLike = setModelLike;
        _setLanguageLike = setLanguageLike;
        Load();
    }

    public ObservableCollection<LanguageInfo> SelectedLanguages { get; } = [];

    public LanguageList Languages { get; } = new();

    public EngineList Engines { get; } = new();

    public string PhrasePauseText => $"{PhrasePauseSeconds:0.0} с";

    public string DraftText => DraftSeconds == 0 ? "выключен" : $"каждые {DraftSeconds} с";

    public bool IsUpdatingEngines => Engines.IsRebuilding;

    public bool IsUpdatingLanguages => Languages.IsRebuilding;

    public void RefreshCatalog(IReadOnlyList<CloudModelInfo> catalog)
    {
        _catalog = catalog;
        ShowEngines();
    }

    public void LanguageSearchClosed()
    {
        Languages.Rebuild(_settings(), SelectedLanguages.Select(language => language.Code));
        SelectedLanguage = null;
    }

    public void EngineSearchClosed()
    {
        if (Engines.IsSearching || SelectedEngine is null)
        {
            ShowEngines();
        }
    }

    partial void OnRetentionChanged(AudioRetention value) => Store();

    partial void OnPhrasePauseSecondsChanged(double value) => Store();

    partial void OnDraftSecondsChanged(int value) => Store();

    partial void OnSelectedEngineChanged(EngineChoice? value)
    {
        if (!_loading && value is not null && !Engines.IsRebuilding)
        {
            Store();
        }
    }

    partial void OnSelectedLanguageChanged(LanguageChoice? value)
    {
        if (!_loading && value is not null && !Languages.IsRebuilding)
        {
            AddLanguage(value.Code);
            SelectedLanguage = null;
            Store();
        }
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
    private void ToggleLike(EngineChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        choice.IsLiked = !choice.IsLiked;
        _setModelLike(choice.Model, choice.IsLiked);
    }

    [RelayCommand]
    private void RemoveLanguage(LanguageInfo? language)
    {
        if (language is not null && SelectedLanguages.Count > 1 && SelectedLanguages.Remove(language))
        {
            Languages.SetPicked(language.Code, false);
            Store();
        }
    }

    [RelayCommand]
    private void MakeDefaultLanguage(LanguageInfo? language)
    {
        if (language is not null && SelectedLanguages.IndexOf(language) > 0)
        {
            SelectedLanguages.Remove(language);
            SelectedLanguages.Insert(0, language);
            Store();
        }
    }

    private void Load()
    {
        _loading = true;
        NewProjectDefaults defaults = _settings().NewProjectDefaults;
        Retention = defaults.AudioRetention;
        PhrasePauseSeconds = defaults.PhrasePauseSeconds;
        DraftSeconds = defaults.LiveDraftSeconds ?? _settings().LiveDraftSeconds;
        SelectedLanguages.Clear();
        foreach (string code in defaults.Languages)
        {
            AddLanguage(code);
        }

        if (SelectedLanguages.Count == 0)
        {
            AddLanguage("ru");
        }

        Languages.Rebuild(_settings(), SelectedLanguages.Select(language => language.Code));
        ShowEngines();
        _loading = false;
    }

    private void Store()
    {
        if (_loading)
        {
            return;
        }

        string? model = SelectedEngine?.Model;
        if (model is null && SelectedEngine is null)
        {
            model = _settings().NewProjectDefaults.CloudModel;
        }

        _save(_settings() with
        {
            NewProjectDefaults = new NewProjectDefaults
            {
                Languages = [ .. SelectedLanguages.Select(language => language.Code) ],
                AudioRetention = Retention,
                LiveDraftSeconds = DraftSeconds,
                PhrasePauseSeconds = Math.Round(PhrasePauseSeconds, 1),
                Engine = model is null ? TranscriptionEngine.Local : TranscriptionEngine.RouterAi,
                CloudModel = model ?? _settings().NewProjectDefaults.CloudModel,
            },
        });
    }

    private void AddLanguage(string code)
    {
        if (LanguageCatalog.Find(code) is not { } language || SelectedLanguages.Any(selected => selected.Code == language.Code))
        {
            return;
        }

        SelectedLanguages.Add(language);
    }

    private void ShowEngines()
    {
        NewProjectDefaults defaults = _settings().NewProjectDefaults;
        string? current = defaults.Engine == TranscriptionEngine.RouterAi ? defaults.CloudModel : null;
        _loading = true;
        Engines.Rebuild(_catalog, _settings(), [ current, defaults.CloudModel ]);
        SelectedEngine = Engines.Find(current);
        Languages.Rebuild(_settings(), SelectedLanguages.Select(language => language.Code));
        _loading = false;
    }
}

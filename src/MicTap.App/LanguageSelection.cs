using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicTap.Core;

namespace MicTap.App;

public sealed partial class LanguageChoice(SupportedLanguage language, bool selected, bool favorite) : ObservableObject
{
    public SupportedLanguage Language { get; } = language;
    public string Label => $"{Language.Name} · {Language.Code}";
    [ObservableProperty] private bool _isSelected = selected;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Star))] private bool _isFavorite = favorite;
    public string Star => IsFavorite ? "★" : "☆";
}

public sealed partial class LanguageSelection : ObservableObject
{
    private readonly Action<IReadOnlyList<string>> _favoritesChanged;
    private readonly List<string> _selected;
    public IReadOnlyList<LanguageChoice> Choices { get; }
    public ObservableCollection<LanguageChoice> Results { get; } = [];
    [ObservableProperty] private string _query = "";
    public IReadOnlyList<string> SelectedCodes => _selected.ToArray();
    public string Summary => _selected.Count == 0 ? "Выберите языки" : string.Join(", ", _selected.Select(code => LanguageCatalog.Find(code)?.Name ?? code));

    public LanguageSelection(IEnumerable<string> selected, IEnumerable<string> favorites, Action<IReadOnlyList<string>> favoritesChanged)
    {
        _selected = [ .. selected.Distinct() ];
        _favoritesChanged = favoritesChanged;
        Choices = [ .. LanguageCatalog.All.Select(language => new LanguageChoice(language, _selected.Contains(language.Code), favorites.Contains(language.Code))) ];
        foreach (LanguageChoice choice in Choices)
        {
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(LanguageChoice.IsSelected)) return;
                if (choice.IsSelected) _selected.Add(choice.Language.Code);
                else _selected.Remove(choice.Language.Code);
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(SelectedCodes));
            };
        }
        Refresh();
    }

    partial void OnQueryChanged(string value) => Refresh();
    public void SetFavorites(IReadOnlyList<string> codes)
    {
        foreach (LanguageChoice choice in Choices) choice.IsFavorite = codes.Contains(choice.Language.Code);
        Refresh();
    }
    [RelayCommand]
    private void ToggleFavorite(LanguageChoice choice)
    {
        choice.IsFavorite = !choice.IsFavorite;
        _favoritesChanged([ .. Choices.Where(c => c.IsFavorite).Select(c => c.Language.Code) ]);
        Refresh();
    }

    private void Refresh()
    {
        Results.Clear();
        foreach (LanguageChoice choice in Choices.Where(c => c.Language.Matches(Query))
                     .OrderByDescending(c => c.IsFavorite).ThenBy(c => c.Language.Name, StringComparer.CurrentCultureIgnoreCase)) Results.Add(choice);
    }
}

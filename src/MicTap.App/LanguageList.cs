using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;

namespace MicTap.App;

/// <summary>A language in a list with search: the star makes it a favorite, so it comes first in every list.</summary>
public sealed partial class LanguageChoice(LanguageInfo language, bool isFavorite, bool isPicked) : ObservableObject
{
    [ObservableProperty]
    private bool _isFavorite = isFavorite;

    [ObservableProperty]
    private bool _isPicked = isPicked;

    public string Code => Language.Code;

    public string Name => Language.RussianName;

    public string EnglishName => Language.EnglishName;

    public LanguageInfo Language { get; } = language;

    public override string ToString() => Language.ToString();
}

/// <summary>
/// Languages for a combo box with search. Typing narrows by Russian name, English name or code; favorites come first.
/// </summary>
public sealed class LanguageList
{
    private string _search = "";

    public LanguageList() => View = new ListCollectionView(Choices)
    {
        Filter = item => _search.Length == 0
            || (item is LanguageChoice choice
                && (choice.Code.Contains(_search, StringComparison.OrdinalIgnoreCase)
                    || choice.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                    || choice.EnglishName.Contains(_search, StringComparison.OrdinalIgnoreCase))),
    };

    public ObservableCollection<LanguageChoice> Choices { get; } = [];

    public ICollectionView View { get; }

    public bool IsSearching => _search.Length > 0;

    public bool IsRebuilding { get; private set; }

    /// <summary>Fills the list anew with the search cleared: favorites first, the picked ones marked.</summary>
    public void Rebuild(AppSettings settings, IEnumerable<string> picked)
    {
        IsRebuilding = true;
        _search = "";
        View.Refresh();
        Choices.Clear();
        HashSet<string> chosen = new(picked, StringComparer.OrdinalIgnoreCase);
        foreach (LanguageInfo language in LanguageCatalog.All
            .OrderByDescending(language => settings.LikesLanguage(language.Code))
            .ThenBy(language => language.RussianName, StringComparer.CurrentCultureIgnoreCase))
        {
            Choices.Add(new LanguageChoice(language, settings.LikesLanguage(language.Code), chosen.Contains(language.Code)));
        }

        IsRebuilding = false;
    }

    public void Search(string text)
    {
        _search = text.Trim();
        View.Refresh();
    }

    public LanguageChoice? Find(string code) => Choices.FirstOrDefault(choice => string.Equals(choice.Code, code, StringComparison.OrdinalIgnoreCase));

    public void SetFavorite(string code, bool favorite)
    {
        foreach (LanguageChoice choice in Choices.Where(choice => string.Equals(choice.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            choice.IsFavorite = favorite;
        }
    }

    public void SetPicked(string code, bool picked)
    {
        foreach (LanguageChoice choice in Choices.Where(choice => string.Equals(choice.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            choice.IsPicked = picked;
        }
    }
}

/// <summary>What a language combo box with search needs from its data context; see MainWindow.OnLanguageSearchChanged.</summary>
public interface ILanguageSearch
{
    LanguageList Languages { get; }

    bool IsUpdatingLanguages { get; }

    void LanguageSearchClosed();
}

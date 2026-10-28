using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;

namespace MicTap.App;

/// <summary>An item of an engine list: <paramref name="model"/> null is the local Whisper on the graphics card.</summary>
public sealed partial class EngineChoice(string? model, string title, string price, bool isLiked) : ObservableObject
{
    [ObservableProperty]
    private bool _isLiked = isLiked;

    public string? Model { get; } = model;

    public string Title { get; } = title;

    public string Price { get; } = price;

    public override string ToString() => $"{Title} — {Price}";
}

/// <summary>
/// The engines a project's phrases can go to, for a combo box with search: the graphics card and Router AI's models with
/// their prices, the liked ones first. What is typed into the box narrows the list by the model's name.
/// </summary>
public sealed class EngineList
{
    private string _search = "";

    public EngineList() => View = new ListCollectionView(Choices)
    {
        Filter = item => _search.Length == 0
            || (item is EngineChoice choice && choice.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)),
    };

    public ObservableCollection<EngineChoice> Choices { get; } = [];

    /// <summary><see cref="Choices"/> as the box lists them: narrowed by what is typed into it.</summary>
    public ICollectionView View { get; }

    public bool IsSearching => _search.Length > 0;

    /// <summary>The list is being filled anew: the box's text changes now are not the user's typing.</summary>
    public bool IsRebuilding { get; private set; }

    /// <summary>
    /// Fills the list anew with the search cleared: the graphics card, the catalog's models and the <paramref name="known"/>
    /// ones it lacks (offline, or gone from it); the models liked in <paramref name="settings"/> first.
    /// </summary>
    public void Rebuild(IReadOnlyList<CloudModelInfo> catalog, AppSettings settings, IEnumerable<string?> known)
    {
        IsRebuilding = true;
        _search = "";
        View.Refresh();
        Choices.Clear();
        List<EngineChoice> choices = [ new EngineChoice(null, "Видеокарта · Whisper", "бесплатно", settings.Likes(null)) ];
        choices.AddRange(catalog.Select(model => new EngineChoice(model.Id, model.Id, PriceText(model.RublesPerMinute), settings.Likes(model.Id))));
        foreach (string model in known.OfType<string>().Concat(settings.LikedModels).Where(m => m != AppSettings.LocalModel).Distinct())
        {
            if (choices.All(choice => choice.Model != model))
            {
                choices.Insert(1, new EngineChoice(model, model, "цена неизвестна", settings.Likes(model)));
            }
        }

        foreach (EngineChoice choice in choices.Where(c => c.IsLiked).Concat(choices.Where(c => !c.IsLiked)))
        {
            Choices.Add(choice);
        }

        IsRebuilding = false;
    }

    /// <summary>Narrows the list to the models whose name has <paramref name="text"/>: «gpt», «eleven», «mistral»…</summary>
    public void Search(string text)
    {
        _search = text.Trim();
        View.Refresh();
    }

    public EngineChoice Find(string? model) => Choices.First(choice => choice.Model == model);

    /// <summary>The heart of <paramref name="model"/> in this list; the order stays as it is until the next rebuild.</summary>
    public void SetLiked(string? model, bool liked)
    {
        foreach (EngineChoice choice in Choices.Where(c => c.Model == model))
        {
            choice.IsLiked = liked;
        }
    }

    private static string PriceText(double? rublesPerMinute) =>
        rublesPerMinute is { } price ? $"{price:0.###} ₽/мин" : "по токенам";
}

/// <summary>What an engine combo box with search needs from its data context; see MainWindow.OnEngineSearchChanged.</summary>
public interface IEngineSearch
{
    EngineList Engines { get; }

    /// <summary>The box's text is being set by the program, not typed.</summary>
    bool IsUpdatingEngines { get; }

    /// <summary>The list closed: a search is over; without a pick the chosen engine shows again.</summary>
    void EngineSearchClosed();
}

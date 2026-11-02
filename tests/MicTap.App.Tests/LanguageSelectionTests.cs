using MicTap.App;

namespace MicTap.App.Tests;

public sealed class LanguageSelectionTests
{
    [Fact]
    public void Multiple_languages_search_and_favorites_are_independent()
    {
        IReadOnlyList<string> saved = [];
        var selection = new LanguageSelection([ "ru" ], [ "ja" ], codes => saved = codes);
        Assert.Equal("ja", selection.Results[ 0 ].Language.Code);
        selection.Query = "a";
        Assert.Equal("ja", selection.Results[ 0 ].Language.Code);
        selection.Choices.Single(c => c.Language.Code == "ja").IsSelected = true;
        Assert.Equal([ "ru", "ja" ], selection.SelectedCodes);
        selection.Query = "not-a-supported-language";
        Assert.Empty(selection.Results);
        Assert.Equal([ "ru", "ja" ], selection.SelectedCodes);
        selection.Query = "Russian";
        Assert.Contains(selection.Results, c => c.Language.Code == "ru");
        selection.ToggleFavoriteCommand.Execute(selection.Choices.Single(c => c.Language.Code == "ru"));
        Assert.Contains("ru", saved);
        Assert.Contains("ja", saved);
    }
}

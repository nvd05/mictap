namespace MicTap.Core.Tests;

public sealed class ProjectDefaultsTests
{
    [Fact]
    public void Defaults_and_language_favorites_survive_restart_without_rewriting_existing_projects()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        ProjectInfo existing = Projects.Create(temp.Path, new ProjectSettings("Старый", [ "ja" ], AudioRetention.Keep));
        string original = File.ReadAllText(existing.SettingsFile);
        AppSettings first = store.Load();
        Assert.Equal([ "ru" ], first.NewProjectDefaults.Languages);
        Assert.Equal(0, first.NewProjectDefaults.LiveDraftSeconds);
        Assert.Equal(3, first.NewProjectDefaults.PhrasePauseSeconds);
        Assert.Equal(AudioRetention.DeleteAfterTranscript, first.NewProjectDefaults.AudioRetention);
        Assert.Equal("mistralai/voxtral-small-transcribe-3-0", first.NewProjectDefaults.CloudModel);
        var changed = first with
        {
            FavoriteLanguages = [ "ja", "en" ],
            NewProjectDefaults = first.NewProjectDefaults with { Languages = [ "ru", "ja" ], PhrasePauseSeconds = 4, LiveDraftSeconds = 7 },
        };
        store.Save(changed);
        AppSettings restarted = new SettingsStore(store.FilePath).Load();
        Assert.Equal(changed, restarted);
        ProjectInfo created = Projects.Create(temp.Path, restarted.NewProjectDefaults.ForProject("Новый"));
        ProjectInfo loaded = Projects.Load(created.Folder);
        Assert.Equal([ "ru", "ja" ], loaded.Settings.Languages);
        Assert.Equal(7, loaded.Settings.LiveDraftSeconds);
        Assert.Equal(4, loaded.Settings.PhrasePauseSeconds);
        Assert.Equal(original, File.ReadAllText(existing.SettingsFile));
        Assert.Null(Projects.Load(existing.Folder).Settings.LiveDraftSeconds);
    }

    [Theory]
    [InlineData("рус")]
    [InlineData("Russian")]
    [InlineData("ru")]
    public void Russian_is_searchable_by_name_and_code(string query) => Assert.True(LanguageCatalog.Find("ru")!.Matches(query));

    [Fact]
    public void Arbitrary_queries_are_not_supported_language_codes()
    {
        Assert.False(LanguageCatalog.Supports("invented"));
        Assert.False(LanguageCatalog.Supports("zz"));
        Assert.True(LanguageCatalog.Supports("ja"));
        Assert.True(LanguageCatalog.All.Count >= 99);
    }
}

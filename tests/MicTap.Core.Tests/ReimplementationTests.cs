namespace MicTap.Core.Tests;

public sealed class LanguageCatalogTests
{
    [Theory]
    [InlineData("рус", "ru")]
    [InlineData("Russian", "ru")]
    [InlineData("ru", "ru")]
    [InlineData("япон", "ja")]
    [InlineData("Japanese", "ja")]
    [InlineData("ja", "ja")]
    public void Search_finds_a_language_by_russian_english_or_code(string query, string code) =>
        Assert.Contains(LanguageCatalog.Search(query), language => language.Code == code);

    [Fact]
    public void Favorites_come_first_and_unknown_codes_are_not_languages()
    {
        IReadOnlyList<LanguageInfo> found = LanguageCatalog.Search("", [ "ja" ]);

        Assert.Equal("ja", found[ 0 ].Code);
        Assert.True(LanguageCatalog.Supports("ru"));
        Assert.False(LanguageCatalog.Supports("xx"));
        Assert.DoesNotContain(LanguageCatalog.Search("xx"), language => language.Code == "xx");
    }
}

public sealed class NewProjectDefaultsTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Defaults_are_the_documented_ones_and_round_trip()
    {
        var store = new SettingsStore(Path.Combine(_temp.Path, "settings.json"));
        AppSettings settings = store.Load();

        Assert.Equal([ "ru" ], settings.NewProjectDefaults.Languages);
        Assert.Equal(0, settings.NewProjectDefaults.LiveDraftSeconds);
        Assert.Equal(AudioRetention.DeleteAfterTranscript, settings.NewProjectDefaults.AudioRetention);
        Assert.Equal(3, settings.NewProjectDefaults.PhrasePauseSeconds);
        Assert.Equal(TranscriptionEngine.RouterAi, settings.NewProjectDefaults.Engine);
        Assert.Equal("mistralai/voxtral-small-transcribe-3-0", settings.NewProjectDefaults.CloudModel);
        Assert.Equal([ "ru", "ja" ], settings.FavoriteLanguages);

        ProjectSettings project = settings.NewProjectDefaults.ForProject("Новый");
        store.Save(settings with { NewProjectDefaults = settings.NewProjectDefaults with { Languages = [ "ja", "ru" ] } });

        Assert.Equal([ "ja", "ru" ], store.Load().NewProjectDefaults.Languages);
        Assert.Equal(3, project.PhrasePauseSeconds);
    }

    [Fact]
    public void Existing_projects_are_not_rewritten_by_new_defaults()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Старый", [ "ja" ], AudioRetention.Keep, 1.2));
        var store = new SettingsStore(Path.Combine(_temp.Path, "settings.json"));
        store.Save(new AppSettings { NewProjectDefaults = new NewProjectDefaults { Languages = [ "ru" ], PhrasePauseSeconds = 3 } });

        ProjectSettings loaded = Projects.Load(project.Folder).Settings;

        Assert.Equal([ "ja" ], loaded.Languages);
        Assert.Equal(1.2, loaded.PhrasePauseSeconds);
    }
}

public sealed class ProjectRenameTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Rename_moves_the_folder_with_its_sessions_and_text()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Старое", [ "ru" ], AudioRetention.Keep));
        SessionInfo session = SessionFolders.Open(project.Folder, "Сессия");
        new JsonlTranscriptSink(session.Folder).Append(0, 1, "ru", "фраза");

        ProjectInfo moved = Projects.Rename(project, "Новое");

        Assert.Equal(Path.Combine(_temp.Path, "Новое"), moved.Folder);
        Assert.Equal("Новое", moved.Settings.Name);
        Assert.False(Directory.Exists(project.Folder));
        Assert.Equal("фраза", Assert.Single(new JsonlTranscriptSink(Path.Combine(moved.Folder, "Сессия")).Lines).Text);
        Assert.Equal("Новое", Projects.Load(moved.Folder).Name);
    }

    [Fact]
    public void Rename_to_a_taken_folder_changes_nothing()
    {
        ProjectInfo first = Projects.Create(_temp.Path, new ProjectSettings("Первый", [ "ru" ], AudioRetention.Keep));
        Projects.Create(_temp.Path, new ProjectSettings("Второй", [ "ru" ], AudioRetention.Keep));

        Assert.Throws<IOException>(() => Projects.Rename(first, "Второй"));
        Assert.True(Directory.Exists(first.Folder));
        Assert.Equal("Первый", Projects.Load(first.Folder).Name);
        Assert.Equal(2, Projects.List(_temp.Path).Count);
    }
}

public sealed class AudioCleanupTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Plan_counts_chunks_and_apply_keeps_the_transcript()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Звук", [ "ru" ], AudioRetention.Keep));
        SessionInfo session = SessionFolders.Open(project.Folder, "Сессия");
        File.WriteAllBytes(Path.Combine(session.AudioFolder, "000.wav"), new byte[ 12 ]);
        File.WriteAllBytes(Path.Combine(session.AudioFolder, "001.wav"), new byte[ 20 ]);
        new JsonlTranscriptSink(session.Folder).Append(0, 1, "ru", "текст");

        AudioCleanupPlan plan = AudioCleanup.Plan(project, AudioRetention.DeleteAfterTranscript);

        Assert.Equal((1, 2, 32L), (plan.Sessions.Count, plan.Files, plan.Bytes));
        Assert.Equal(2, plan.Apply());
        Assert.Empty(Directory.EnumerateFiles(session.AudioFolder, "*.wav"));
        Assert.Equal("текст", Assert.Single(new JsonlTranscriptSink(session.Folder).Lines).Text);
        Assert.True(File.Exists(session.MetaFile) is false);
    }

    [Fact]
    public void Keep_plans_nothing()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Звук", [ "ru" ], AudioRetention.Keep));
        SessionInfo session = SessionFolders.Open(project.Folder, "Сессия");
        File.WriteAllBytes(Path.Combine(session.AudioFolder, "000.wav"), new byte[ 12 ]);

        Assert.True(AudioCleanup.Plan(project, AudioRetention.Keep).IsEmpty);
    }
}

public sealed class ManualTextTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Text_session_has_meta_without_starts_and_holds_manual_lines_in_order()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Текст", [ "ru" ], AudioRetention.None));
        SessionInfo session = SessionFolders.OpenText(project.Folder, "заметки", "ru", "test");

        var sink = new JsonlTranscriptSink(session.Folder);
        sink.Append(0, 0, "ru", "первая");
        sink.Append(0, 0, "ru", "вторая");

        SessionMeta meta = SessionMeta.Read(session, "ru");
        Assert.Empty(meta.Starts);
        Assert.Null(meta.Started);
        Assert.Equal([ "первая", "вторая" ], sink.Lines.Select(line => line.Text));
        Assert.Single(SessionFolders.List(project.Folder));
    }
}

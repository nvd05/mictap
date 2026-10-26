using System.Text.Json;

namespace MicTap.Core.Tests;

public sealed class ProjectsTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Project_json_round_trips_with_schema_version_first()
    {
        var settings = new ProjectSettings("Японский", ["ja", "ru"], AudioRetention.DeleteAfterTranscript);

        ProjectInfo project = Projects.Create(_temp.Path, settings);
        ProjectInfo loaded = Projects.Load(project.Folder);

        Assert.Equal(Path.Combine(_temp.Path, "Японский"), project.Folder);
        Assert.Equal(settings.Name, loaded.Settings.Name);
        Assert.Equal(settings.Languages, loaded.Settings.Languages);
        Assert.Equal(AudioRetention.DeleteAfterTranscript, loaded.Settings.AudioRetention);
        Assert.Equal("ja", loaded.Settings.DefaultLanguage);
        JsonElement root = JsonDocument.Parse(File.ReadAllText(project.SettingsFile)).RootElement;
        Assert.Equal(["schemaVersion", "name", "languages", "audioRetention"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("deleteAfterTranscript", root.GetProperty("audioRetention").GetString());
    }

    [Fact]
    public void Projects_are_listed_by_name_and_folders_without_project_json_ignored()
    {
        Projects.Create(_temp.Path, new ProjectSettings("Матан", ["ru"], AudioRetention.Keep));
        Projects.Create(_temp.Path, new ProjectSettings("Английский", ["en", "ru"], AudioRetention.None));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "2026-10-26_16-08-00"));

        Assert.Equal(["Английский", "Матан"], Projects.List(_temp.Path).Select(p => p.Name));
    }

    [Fact]
    public void Same_name_twice_gets_its_own_folder_and_unsafe_characters_are_dropped()
    {
        ProjectInfo first = Projects.Create(_temp.Path, new ProjectSettings("Физика: лабы", ["ru"], AudioRetention.Keep));
        ProjectInfo second = Projects.Create(_temp.Path, new ProjectSettings("Физика: лабы", ["ru"], AudioRetention.Keep));

        Assert.Equal("Физика лабы", Path.GetFileName(first.Folder));
        Assert.Equal("Физика лабы_2", Path.GetFileName(second.Folder));
        Assert.Equal("Физика: лабы", second.Name);
    }

    [Fact]
    public void Saved_settings_keep_the_folder()
    {
        ProjectInfo project = Projects.Create(_temp.Path, new ProjectSettings("Японский", ["ja"], AudioRetention.Keep));

        ProjectInfo saved = Projects.Save(project, project.Settings with { Languages = ["ja", "ru"], AudioRetention = AudioRetention.None });

        Assert.Equal(project.Folder, saved.Folder);
        Assert.Equal(["ja", "ru"], Projects.Load(project.Folder).Settings.Languages);
        Assert.Equal(AudioRetention.None, Projects.Load(project.Folder).Settings.AudioRetention);
    }

    [Theory]
    [InlineData("", "ru")]
    [InlineData("Проект", "")]
    [InlineData("Проект", "russian")]
    [InlineData("Проект", "ru, ru")]
    public void Invalid_settings_are_refused(string name, string languages)
    {
        Assert.Throws<ArgumentException>(
            () => Projects.Create(_temp.Path, new ProjectSettings(name, Projects.ParseLanguages(languages), AudioRetention.Keep)));
    }

    [Fact]
    public void Languages_are_parsed_from_a_typed_list()
    {
        Assert.Equal(["ja", "ru", "en"], Projects.ParseLanguages(" JA, ru;en "));
    }

    [Fact]
    public void Broken_project_json_is_reported_with_its_path()
    {
        string folder = Path.Combine(_temp.Path, "Сломан");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "project.json"), """{ "name": "Сломан", "languages": [], "audioRetention": "keep" }""");

        var error = Assert.Throws<InvalidDataException>(() => Projects.Load(folder));

        Assert.Contains(Path.Combine(folder, "project.json"), error.Message);
    }
}

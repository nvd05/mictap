using System.Text.Json;

namespace MicTap.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Saved_settings_load_back_equal()
    {
        var store = new SettingsStore(Path.Combine(_temp.Path, "mictap", "settings.json"));
        var settings = new AppSettings
        {
            RootFolder = @"D:\Записи",
            DeviceName = "Микрофон (Realtek(R) Audio)",
            ChunkSeconds = 90,
        };

        store.Save(settings);

        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void File_uses_documented_property_names()
    {
        var store = new SettingsStore(Path.Combine(_temp.Path, "settings.json"));

        store.Save(new AppSettings { RootFolder = @"D:\rec", DeviceName = "Mic" });

        using var json = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        JsonElement root = json.RootElement;
        Assert.Equal(@"D:\rec", root.GetProperty("rootFolder").GetString());
        Assert.Equal("Mic", root.GetProperty("deviceName").GetString());
        Assert.Equal(60, root.GetProperty("chunkSeconds").GetInt32());
        Assert.Equal("Ctrl+Alt+L", root.GetProperty("hotkeys").GetProperty("toggleLanguage").GetString());
        Assert.Equal("Ctrl+Alt+R", root.GetProperty("hotkeys").GetProperty("toggleRecording").GetString());
        Assert.Equal(47310, root.GetProperty("hubPort").GetInt32());
        Assert.Contains("\"Ctrl+Alt+L\"", File.ReadAllText(store.FilePath)); // editable by hand, not "Ctrl+Alt..."
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        AppSettings settings = new SettingsStore(Path.Combine(_temp.Path, "absent.json")).Load();

        Assert.Null(settings.RootFolder);
        Assert.Null(settings.DeviceName);
        Assert.Equal(60, settings.ChunkSeconds);
    }

    [Fact]
    public void Missing_chunk_seconds_defaults_to_60()
    {
        string path = Path.Combine(_temp.Path, "settings.json");
        File.WriteAllText(path, """{ "rootFolder": "D:\\rec" }""");

        AppSettings settings = new SettingsStore(path).Load();

        Assert.Equal(@"D:\rec", settings.RootFolder);
        Assert.Equal(60, settings.ChunkSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Out_of_range_chunk_seconds_is_rejected(int chunkSeconds)
    {
        string path = Path.Combine(_temp.Path, "settings.json");
        File.WriteAllText(path, $$"""{ "chunkSeconds": {{chunkSeconds}} }""");

        Assert.Throws<InvalidDataException>(() => new SettingsStore(path).Load());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public void Out_of_range_live_draft_seconds_is_rejected(int seconds)
    {
        string path = Path.Combine(_temp.Path, "settings.json");
        File.WriteAllText(path, $$"""{ "liveDraftSeconds": {{seconds}} }""");

        Assert.Throws<InvalidDataException>(() => new SettingsStore(path).Load());
    }

    [Fact]
    public void Live_draft_is_every_3_seconds_unless_set_and_0_turns_it_off()
    {
        string path = Path.Combine(_temp.Path, "settings.json");
        var store = new SettingsStore(path);

        int byDefault = store.Load().LiveDraftSeconds;
        store.Save(store.Load() with { LiveDraftSeconds = 0 });

        Assert.Equal((3, 0), (byDefault, store.Load().LiveDraftSeconds));
    }

    [Fact]
    public void Hotkey_left_out_of_the_file_keeps_its_default()
    {
        string path = Path.Combine(_temp.Path, "settings.json");
        File.WriteAllText(path, """{ "hotkeys": { "toggleLanguage": "Ctrl+Alt+J" } }""");

        AppSettings settings = new SettingsStore(path).Load();

        Assert.Equal(
            new HotkeySettings { ToggleLanguage = "Ctrl+Alt+J", ToggleRecording = "Ctrl+Alt+R", ForcePush = "Ctrl+Alt+Enter" },
            settings.Hotkeys);
    }

    [Fact]
    public void Malformed_json_is_reported_with_the_file_path()
    {
        string path = Path.Combine(_temp.Path, "settings.json");
        File.WriteAllText(path, "{ not json");

        var error = Assert.Throws<InvalidDataException>(() => new SettingsStore(path).Load());

        Assert.Contains(path, error.Message);
    }
}

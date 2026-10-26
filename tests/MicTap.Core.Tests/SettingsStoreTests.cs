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
            Language = "ja",
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
        Assert.Equal("ru", root.GetProperty("language").GetString());
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

    [Fact]
    public void Unknown_language_is_rejected()
    {
        string path = Path.Combine(_temp.Path, "settings.json");
        File.WriteAllText(path, """{ "language": "en" }""");

        var error = Assert.Throws<InvalidDataException>(() => new SettingsStore(path).Load());

        Assert.Contains("language", error.Message);
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

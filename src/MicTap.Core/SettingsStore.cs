using System.Text.Json;

namespace MicTap.Core;

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore(string filePath)
{
    public string FilePath { get; } = filePath;

    /// <summary>Returns the saved settings, or defaults when nothing has been saved yet.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonFiles.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Settings file {FilePath} is not valid JSON: {ex.Message}", ex);
        }

        if (settings is null)
        {
            throw new InvalidDataException($"Settings file {FilePath} contains null.");
        }

        Validate(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Validate(settings);
        JsonFiles.Write(FilePath, settings);
    }

    private void Validate(AppSettings settings)
    {
        if (settings.ChunkSeconds is < 1 or > AppSettings.MaxChunkSeconds)
        {
            throw new InvalidDataException(
                $"Settings file {FilePath}: chunkSeconds must be 1..{AppSettings.MaxChunkSeconds}, got {settings.ChunkSeconds}.");
        }

        if (settings.HubPort is < 1 or > 65535)
        {
            throw new InvalidDataException($"Settings file {FilePath}: hubPort must be 1..65535, got {settings.HubPort}.");
        }
    }
}

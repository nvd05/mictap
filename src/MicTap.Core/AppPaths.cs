namespace MicTap.Core;

/// <summary>
/// Per-user locations under %APPDATA%\mictap: settings.json, logs\ and models\. A profile (<c>--profile &lt;folder&gt;</c>,
/// for tests) keeps settings and logs in its own folder, so the user's settings stay untouched; the models are shared.
/// </summary>
public static class AppPaths
{
    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mictap");

    /// <summary>The profile folder, or null for the user's own settings.</summary>
    public static string? Profile { get; private set; }

    public static string DataFolder => Profile ?? DefaultFolder;

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string LogsFolder => Path.Combine(DataFolder, "logs");

    /// <summary>Whisper and Silero models: 1.6 GB, shared by every profile.</summary>
    public static string ModelsFolder => Path.Combine(DefaultFolder, "models");

    /// <summary>Settings and logs go to <paramref name="folder"/> from now on. Called once, at start.</summary>
    public static void UseProfile(string folder) => Profile = Path.GetFullPath(folder);
}

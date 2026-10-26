namespace MicTap.Core;

/// <summary>Per-user locations: %APPDATA%\mictap\settings.json and %APPDATA%\mictap\logs\.</summary>
public static class AppPaths
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mictap");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string LogsFolder => Path.Combine(DataFolder, "logs");
}

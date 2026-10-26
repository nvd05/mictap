namespace MicTap.Core;

/// <summary>Per-user locations under %APPDATA%\mictap: settings.json, logs\ and models\.</summary>
public static class AppPaths
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mictap");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string LogsFolder => Path.Combine(DataFolder, "logs");

    public static string ModelsFolder => Path.Combine(DataFolder, "models");
}

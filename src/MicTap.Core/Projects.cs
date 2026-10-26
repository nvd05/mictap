using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MicTap.Core;

/// <summary>What happens to the audio of a session. The text is always kept.</summary>
public enum AudioRetention
{
    /// <summary>Chunks stay in audio\.</summary>
    Keep,

    /// <summary>A chunk is deleted once all its speech is in transcript.jsonl on disk.</summary>
    DeleteAfterTranscript,

    /// <summary>No audio is written; recognizing a session again is impossible.</summary>
    None,
}

/// <summary>
/// Contents of project.json. The first language is the default; the language hotkey goes round the list.
/// </summary>
public sealed record ProjectSettings(string Name, IReadOnlyList<string> Languages, AudioRetention AudioRetention)
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion => CurrentSchemaVersion;

    [JsonIgnore]
    public string DefaultLanguage => Languages[0];
}

/// <summary>A project folder under the root: &lt;root&gt;\&lt;project&gt;\project.json and its sessions.</summary>
public sealed record ProjectInfo(string Folder, ProjectSettings Settings)
{
    public string Name => Settings.Name;

    public string SettingsFile => Path.Combine(Folder, Projects.FileName);
}

public static partial class Projects
{
    public const string FileName = "project.json";

    /// <summary>Root of projects unless settings.json names another: %LOCALAPPDATA%\mictap\projects.</summary>
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mictap", "projects");

    /// <summary>Projects under <paramref name="root"/>: subfolders with project.json, by name. Other folders are ignored.</summary>
    public static IReadOnlyList<ProjectInfo> List(string root) =>
        !Directory.Exists(root)
            ? [ ]
            :
            [
                .. Directory.EnumerateDirectories(root)
                    .Where(folder => File.Exists(Path.Combine(folder, FileName)))
                    .Select(Load)
                    .OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase),
            ];

    /// <summary>Creates a project in a folder named after it (with _2, _3 … if that folder exists).</summary>
    public static ProjectInfo Create(string root, ProjectSettings settings)
    {
        Validate(settings);
        string baseName = SessionFolders.Sanitize(settings.Name);
        if (baseName.Length == 0)
        {
            throw new ArgumentException($"«{settings.Name}» не годится как имя папки.");
        }

        for (int attempt = 1; ; attempt++)
        {
            string folder = Path.Combine(root, attempt == 1 ? baseName : $"{baseName}_{attempt}");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                var project = new ProjectInfo(folder, settings);
                JsonFiles.Write(project.SettingsFile, settings);
                return project;
            }
        }
    }

    public static ProjectInfo Load(string folder)
    {
        string file = Path.Combine(folder, FileName);
        ProjectSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<ProjectSettings>(File.ReadAllText(file), JsonFiles.Options)
                ?? throw new InvalidDataException($"{file} contains null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{file} is not a valid project: {ex.Message}", ex);
        }

        try
        {
            Validate(settings);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"{file}: {ex.Message}", ex);
        }

        return new ProjectInfo(folder, settings);
    }

    /// <summary>Saves new settings of a project; its folder keeps its name.</summary>
    public static ProjectInfo Save(ProjectInfo project, ProjectSettings settings)
    {
        Validate(settings);
        var saved = project with { Settings = settings };
        JsonFiles.Write(saved.SettingsFile, settings);
        return saved;
    }

    /// <summary>The project made when the root has none yet, so recording works from the first start.</summary>
    public static ProjectSettings FirstProject { get; } = new("Общий", [ "ru", "ja" ], AudioRetention.Keep);

    /// <summary>Parses "ja, ru" into language codes.</summary>
    public static IReadOnlyList<string> ParseLanguages(string text) =>
        [ .. text.Split([ ',', ' ', ';' ], StringSplitOptions.RemoveEmptyEntries).Select(code => code.Trim().ToLowerInvariant()) ];

    /// <exception cref="ArgumentException">The settings are not usable; the message says why, in Russian.</exception>
    public static void Validate(ProjectSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            throw new ArgumentException("У проекта должно быть имя.");
        }

        if (settings.Languages is null || settings.Languages.Count == 0)
        {
            throw new ArgumentException("Нужен хотя бы один язык, например ru.");
        }

        if (settings.Languages.FirstOrDefault(code => !LanguageCode().IsMatch(code)) is { } bad)
        {
            throw new ArgumentException($"«{bad}» — не код языка; нужны коды вроде ru, ja, en.");
        }

        if (settings.Languages.Distinct().Count() != settings.Languages.Count)
        {
            throw new ArgumentException("Языки повторяются.");
        }
    }

    [GeneratedRegex("^[a-z]{2,3}$")]
    private static partial Regex LanguageCode();
}

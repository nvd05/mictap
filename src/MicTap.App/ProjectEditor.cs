using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;

namespace MicTap.App;

public sealed record RetentionOption(AudioRetention Value, string Name);

/// <summary>The project dialog: a new project, or the settings of an existing one (its name stays).</summary>
public sealed partial class ProjectEditor : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _languagesText = "ru, ja";

    [ObservableProperty]
    private AudioRetention _retention = AudioRetention.Keep;

    [ObservableProperty]
    private string _error = "";

    private ProjectEditor(bool isNew) => IsNew = isNew;

    public static IReadOnlyList<RetentionOption> RetentionOptions { get; } =
    [
        new(AudioRetention.Keep, "Хранить"),
        new(AudioRetention.DeleteAfterTranscript, "Удалять после распознавания"),
        new(AudioRetention.None, "Не сохранять (распознать заново будет нельзя)"),
    ];

    public bool IsNew { get; }

    public string Title => IsNew ? "Новый проект" : "Настройки проекта";

    public static ProjectEditor ForNew() => new(isNew: true);

    public static ProjectEditor For(ProjectInfo project) => new(isNew: false)
    {
        Name = project.Name,
        LanguagesText = string.Join(", ", project.Settings.Languages),
        Retention = project.Settings.AudioRetention,
    };

    /// <summary>The settings typed in; on a mistake <see cref="Error"/> says what is wrong and the result is null.</summary>
    public ProjectSettings? Build()
    {
        var settings = new ProjectSettings(Name.Trim(), Projects.ParseLanguages(LanguagesText), Retention);
        try
        {
            Projects.Validate(settings);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return null;
        }

        Error = "";
        return settings;
    }
}

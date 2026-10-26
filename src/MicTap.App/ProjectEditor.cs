using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;

namespace MicTap.App;

public sealed record RetentionOption(AudioRetention Value, string Name)
{
    /// <summary>What screen readers and UI Automation call the item.</summary>
    public override string ToString() => Name;
}

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
    [NotifyPropertyChangedFor(nameof(PhrasePauseText))]
    private double _phrasePauseSeconds = ProjectSettings.DefaultPhrasePauseSeconds;

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

    public string PhrasePauseText => $"{PhrasePauseSeconds:0.0} с";

    public static ProjectEditor ForNew() => new(isNew: true);

    public static ProjectEditor For(ProjectInfo project) => new(isNew: false)
    {
        Name = project.Name,
        LanguagesText = string.Join(", ", project.Settings.Languages),
        Retention = project.Settings.AudioRetention,
        PhrasePauseSeconds = project.Settings.PhrasePauseSeconds,
    };

    /// <summary>The settings typed in; on a mistake <see cref="Error"/> says what is wrong and the result is null.</summary>
    public ProjectSettings? Build()
    {
        var settings = new ProjectSettings(
            Name.Trim(), Projects.ParseLanguages(LanguagesText), Retention, Math.Round(PhrasePauseSeconds, 1));
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

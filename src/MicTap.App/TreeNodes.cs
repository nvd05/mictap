using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;

namespace MicTap.App;

/// <summary>A project in the side tree; its sessions are the children.</summary>
public sealed partial class ProjectNode : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(RetentionText))]
    private ProjectInfo _project;

    public ProjectNode(ProjectInfo project)
    {
        _project = project;
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSessions));
    }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Agents present in the project's sessions.</summary>
    [ObservableProperty]
    private int _agentCount;

    /// <summary>Not listed unless the hidden projects are shown; see <see cref="AppSettings.HiddenProjects"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HideText))]
    private bool _isHidden;

    /// <summary>Listed first; see <see cref="AppSettings.PinnedProjects"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinText))]
    private bool _isPinned;

    public string Name => Project.Name;

    /// <summary>The project's folder under the root: what settings.json keeps for hidden and pinned projects.</summary>
    public string FolderName => Path.GetFileName(Project.Folder);

    public string PinText => IsPinned ? "Открепить" : "Закрепить наверху";

    public string HideText => IsHidden ? "Показать проект" : "Скрыть проект";

    public string RetentionText => Project.Settings.AudioRetention switch
    {
        AudioRetention.Keep => "Звук хранится в audio\\.",
        AudioRetention.DeleteAfterTranscript => "Звук удаляется, как только фраза записана в текст.",
        _ => "Звук не сохраняется: «Распознать заново» недоступно.",
    } + $"\nФраза заканчивается после паузы {Project.Settings.PhrasePauseSeconds:0.0} с."
      + (Project.Settings.CloudModelInUse is { } model ? $"\nФразы распознаёт Router AI: {model}." : "")
      + CloudSpentText;

    /// <summary>What the project's sessions spent in Router AI, when they spent anything.</summary>
    private string CloudSpentText
    {
        get
        {
            try
            {
                CloudUsage total = CloudUsageLog.Total(Project.Folder);
                return total.Requests == 0 ? "" : $"\nRouter AI: потрачено {total.Rubles:0.00} ₽ во всех сессиях проекта.";
            }
            catch (System.IO.InvalidDataException ex)
            {
                return $"\nRouter AI: расход не прочитан — {ex.Message}";
            }
        }
    }

    public ObservableCollection<SessionNode> Sessions { get; } = [];

    /// <summary>The project can be folded: hovering it shows a chevron instead of the folder.</summary>
    public bool HasSessions => Sessions.Count > 0;
}

/// <summary>A session in the side tree, with the agents reading it.</summary>
public sealed partial class SessionNode(SessionInfo session, ProjectNode project) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    private SessionInfo _session = session;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRecording;

    /// <summary>Agents waiting on the session or thinking after their last call (not gone).</summary>
    [ObservableProperty]
    private int _agentCount;

    [ObservableProperty]
    private string _agentsText = "";

    public ProjectNode Project { get; } = project;

    public string Name => Session.Name;
}

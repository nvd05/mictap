using System.Collections.ObjectModel;
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

    public string Name => Project.Name;

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
                return total.Requests == 0 ? "" : $"\nRouter AI: потрачено {total.Rubles:0.00} ₽ за {total.Seconds / 60:0.#} мин звука.";
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

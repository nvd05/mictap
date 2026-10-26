using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;

namespace MicTap.App;

/// <summary>A project in the side tree; its sessions are the children.</summary>
public sealed partial class ProjectNode(ProjectInfo project) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(RetentionText))]
    private ProjectInfo _project = project;

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
    };

    public ObservableCollection<SessionNode> Sessions { get; } = [ ];
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

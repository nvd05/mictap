using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicTap.Core;
using MicTap.Hub;
using Microsoft.Win32;
using Serilog;

namespace MicTap.App;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan InfoShownFor = TimeSpan.FromSeconds(8);

    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        [ "ru" ] = "Русский",
        [ "ja" ] = "日本語",
        [ "en" ] = "English",
        [ "de" ] = "Deutsch",
        [ "fr" ] = "Français",
        [ "es" ] = "Español",
        [ "it" ] = "Italiano",
        [ "uk" ] = "Українська",
        [ "zh" ] = "中文",
        [ "ko" ] = "한국어",
    };

    private readonly SettingsStore _settingsStore;
    private readonly Recorder _recorder;
    private readonly string _appVersion;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _infoTimer;
    private AppSettings _settings;
    private bool _refreshingDevices;
    private bool _quietLanguage = true; // no tone and no mark while the language follows the selected project
    private bool _quietEngine; // the engine box follows the selected project without saving it back
    private IReadOnlyList<CloudModelInfo> _cloudModels = [];
    private bool _selecting;
    private AgentsState? _agentsState;
    private SessionNode? _recordingNode;
    private long _sessionOffsetTicks; // where the recording starts in its session: the timer shows session time

    [ObservableProperty]
    private string _rootFolder = Core.Projects.DefaultRoot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDevice? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(CanEditSettings), nameof(IsIdle), nameof(TrayToolTip), nameof(EmptyText), nameof(SessionNote), nameof(SessionHeader),
        nameof(HasRecordingAgents))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(StopCommand), nameof(ChooseFolderCommand), nameof(RefreshDevicesCommand),
        nameof(RetranscribeCommand), nameof(NewProjectCommand), nameof(EditProjectCommand), nameof(NewSessionCommand),
        nameof(RenameSessionCommand), nameof(SendToAgentsCommand), nameof(DeleteSessionCommand), nameof(DeleteProjectCommand))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(ChooseFolderCommand), nameof(RefreshDevicesCommand), nameof(RetranscribeCommand),
        nameof(NewProjectCommand), nameof(EditProjectCommand), nameof(NewSessionCommand), nameof(RenameSessionCommand),
        nameof(DeleteSessionCommand), nameof(DeleteProjectCommand))]
    private bool _isStarting;

    [ObservableProperty]
    private double _level;

    [ObservableProperty]
    private EngineChoice? _selectedEngine;

    [ObservableProperty]
    private DraftOption? _selectedDraft;

    [ObservableProperty]
    private ArchiveOption? _selectedArchive;

    /// <summary>A Router AI key is saved; the settings show it masked with «Заменить» and «Удалить».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRouterAiKeyInput), nameof(ShowRouterAiKeyActions))]
    private bool _hasRouterAiKey;

    /// <summary>The user chose «Заменить»: the input shows although a key is saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRouterAiKeyInput), nameof(ShowRouterAiKeyActions))]
    private bool _isReplacingRouterAiKey;

    /// <summary>Something is typed in the key box: only then is there anything to save.</summary>
    [ObservableProperty]
    private bool _canSaveRouterAiKey;

    /// <summary>Agents working with the recording, waiting or thinking: the send button is there only for them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecordingAgents), nameof(SendToAgentsTitle), nameof(SendToAgentsToolTip))]
    [NotifyCanExecuteChangedFor(nameof(SendToAgentsCommand))]
    private int _recordingAgents;

    /// <summary>Whether a Router AI key is saved, as the settings dialog says it.</summary>
    [ObservableProperty]
    private string _routerAiKeyText = "";

    [ObservableProperty]
    private string _sessionText = "—";

    [ObservableProperty]
    private string _chunkText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayToolTip))]
    private string _recordedText = FormatMinutes(TimeSpan.Zero);

    [ObservableProperty]
    private string _elapsedText = FormatElapsed(TimeSpan.Zero);

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(TrayToolTip), nameof(EmptyText), nameof(SessionNote))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(EditProjectCommand), nameof(NewSessionCommand), nameof(RetranscribeCommand),
        nameof(RenameSessionCommand), nameof(DeleteProjectCommand))]
    private ProjectInfo? _selectedProject;

    /// <summary>The session shown in the transcript and recognized again by «Распознать заново».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionHeader), nameof(SessionNote), nameof(EmptyText))]
    [NotifyCanExecuteChangedFor(nameof(RetranscribeCommand), nameof(RenameSessionCommand))]
    private SessionInfo? _selectedSession;

    /// <summary>The tree node of <see cref="SelectedSession"/>, for the session menu above the text.</summary>
    [ObservableProperty]
    private SessionNode? _selectedNode;

    /// <summary>
    /// Name of the session the next recording goes to; empty means its start time. The name of an existing session
    /// appends to it. During a recording a new name is applied to the folder once the recording stops.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionHeader), nameof(SessionNote))]
    private string _sessionName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageText), nameof(TrayToolTip))]
    private string _language = "ru";

    [ObservableProperty]
    private string _hubText = "запуск…";

    [ObservableProperty]
    private bool _hubWarning;

    public MainViewModel(
        SettingsStore settingsStore, AppSettings settings, Recorder recorder, TranscriptViewModel transcript, string appVersion, ILogger logger)
    {
        Transcript = transcript;
        _settingsStore = settingsStore;
        _settings = settings;
        _recorder = recorder;
        _appVersion = appVersion;
        _logger = logger.ForContext<MainViewModel>();
        RootFolder = settings.RootFolder ?? Core.Projects.DefaultRoot;
        _infoTimer = new DispatcherTimer(InfoShownFor, DispatcherPriority.Background, (_, _) => ClearInfo(), _dispatcher);
        _infoTimer.Stop();
        recorder.LevelChanged += OnLevelChanged;
        recorder.ProgressChanged += OnProgressChanged;
        recorder.Stopped += OnRecordingStopped;
        recorder.Started += (_, started) => Interlocked.Exchange(ref _sessionOffsetTicks, started.Offset.Ticks);
        transcript.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptViewModel.IsRetranscribing))
            {
                RetranscribeCommand.NotifyCanExecuteChanged();
                DeleteSessionCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>Recording stopped on its own; the argument explains why.</summary>
    public event EventHandler<string>? RecordingFailed;

    /// <summary>The window shows the project dialog for a new project.</summary>
    public event EventHandler? NewProjectRequested;

    /// <summary>The window shows the project dialog for the settings of this project.</summary>
    public event EventHandler<ProjectInfo>? EditProjectRequested;

    /// <summary>The window asks for a new name of this session; null is the new session the next recording makes.</summary>
    public event EventHandler<SessionNode?>? RenameSessionRequested;

    /// <summary>A project or a session is to go to the archive once the user says yes.</summary>
    public event EventHandler<DeleteRequest>? DeleteRequested;

    public event EventHandler? ArchiveRequested;

    /// <summary>The window shows the settings dialog.</summary>
    public event EventHandler? SettingsRequested;

    public ObservableCollection<AudioDevice> Devices { get; } = [];

    /// <summary>Projects with their sessions, for the side tree.</summary>
    public ObservableCollection<ProjectNode> Projects { get; } = [];

    /// <summary>Languages of the selected project; the first one is its default.</summary>
    public ObservableCollection<LanguageOption> Languages { get; } = [];

    public static IReadOnlyList<ArchiveOption> ArchiveOptions { get; } =
    [
        new(7, "7 дней"),
        new(30, "30 дней"),
        new(90, "90 дней"),
        new(365, "Год"),
        new(0, "Всегда — удалять только руками"),
    ];

    /// <summary>What the archive holds, for its dialog.</summary>
    public ObservableCollection<ArchiveRow> ArchiveRows { get; } = [];

    public static IReadOnlyList<DraftOption> DraftOptions { get; } =
    [
        new(3, "Каждые 3 с — быстро"),
        new(10, "Каждые 10 с — экономнее"),
        new(0, "Выключен — видеокарта только для готовых фраз"),
    ];

    public bool ShowRouterAiKeyInput => !HasRouterAiKey || IsReplacingRouterAiKey;

    public bool ShowRouterAiKeyActions => HasRouterAiKey && !IsReplacingRouterAiKey;

    /// <summary>Who recognizes the phrases of the selected project: the graphics card or a Router AI model with its price.</summary>
    public ObservableCollection<EngineChoice> EngineChoices { get; } = [];

    /// <summary>Agents of the shown session: waiting, thinking or gone.</summary>
    public ObservableCollection<AgentRow> Agents { get; } = [];

    public TranscriptViewModel Transcript { get; }

    public bool CanEditSettings => !IsRecording && !IsStarting;

    public bool IsIdle => !IsRecording;

    /// <summary>The project in the app bar; the session is named above its text.</summary>
    public string Title => SelectedProject?.Name ?? "";

    /// <summary>Name of the shown session, or of the new one the next recording makes.</summary>
    public string SessionHeader =>
        SelectedSession is null ? PendingName is { Length: > 0 } pending ? pending : "Новая сессия"
        : SelectedNode is not null && SelectedNode == _recordingNode && PendingName is { Length: > 0 } renamed && renamed != SelectedSession.Name
            ? renamed
        : SelectedSession.Name;

    /// <summary>A line under the session name: what happens to the name.</summary>
    public string SessionNote =>
        SelectedProject is null ? ""
        : SelectedSession is null
            ? PendingName.Length > 0 && File.Exists(new SessionInfo(PendingName, Path.Combine(SelectedProject.Folder, PendingName)).MetaFile)
                ? $"Проект «{SelectedProject.Name}». Такая сессия есть: запись допишется в неё."
                : $"Проект «{SelectedProject.Name}». " + (PendingName.Length > 0 ? "Имя задано заранее." : "Имя — время начала записи.")
        : SelectedNode is not null && SelectedNode == _recordingNode && PendingName.Length > 0 && PendingName != SelectedSession.Name
            ? "Папка получит это имя после остановки записи."
        : "";

    /// <summary>What the empty transcript says.</summary>
    public string EmptyText =>
        SelectedProject is null ? "Создайте проект слева."
        : IsRecording && SelectedNode is not null && SelectedNode == _recordingNode ? "Говорите — готовые фразы появятся здесь.\nТо, что звучит сейчас, — серым в панели записи."
        : SelectedSession is null ? "Готовые фразы новой сессии появятся здесь."
        : "В этой сессии пока нет текста.";

    public string LanguageText => Language.ToUpperInvariant();

    /// <summary>The label of the language box names its hotkey, as the draft names the recording one.</summary>
    public string LanguageHint => $"Язык · {Hotkeys.ToggleLanguage}";

    public string LanguageToolTip => $"Следующий язык проекта — {Hotkeys.ToggleLanguage}, работает и из трея.";

    public string IdleDraftText =>
        $"Нажмите запись или {Hotkeys.ToggleRecording} — здесь появится то, что вы говорите. {Hotkeys.ForcePush} — отправить агенту.";

    public bool HasRecordingAgents => IsRecording && RecordingAgents > 0;

    public string SendToAgentsTitle => RecordingAgents > 1 ? "Отправить агентам сейчас" : "Отправить агенту сейчас";

    public string SendToAgentsToolTip =>
        $"{SendToAgentsTitle} — {Hotkeys.ForcePush}, во время записи и из трея.\n"
        + (RecordingAgents > 1
            ? "Фраза, которую вы договорили, сразу уходит в текст, агенты получают всё, что ещё не читали."
            : "Фраза, которую вы договорили, сразу уходит в текст, агент получает всё, что ещё не читал.");

    public HotkeySettings Hotkeys => _settings.Hotkeys;

    public string SettingsFile => _settingsStore.FilePath;

    public string TrayToolTip => IsRecording
        ? $"mictap: идёт запись, {RecordedText}, язык {LanguageText}"
        : $"mictap: запись не идёт, язык {LanguageText}";

    private string PendingName => SessionFolders.Sanitize(SessionName);

    /// <summary>Lists microphones and projects and repairs chunks that a crash left open.</summary>
    public void Initialize()
    {
        RefreshDevices();
        LoadProjects();
        RepairProjects();
        _ = LoadCloudModelsAsync();
        SelectedArchive = ArchiveOptions.FirstOrDefault(o => o.Days == _settings.ArchiveDays)
            ?? new ArchiveOption(_settings.ArchiveDays, $"{_settings.ArchiveDays} дн. (из settings.json)");
        PurgeArchive();
        _ = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => PurgeArchive(), _dispatcher);
        SelectedDraft = DraftOptions.FirstOrDefault(o => o.Seconds == _settings.LiveDraftSeconds)
            ?? new DraftOption(_settings.LiveDraftSeconds, $"Каждые {_settings.LiveDraftSeconds} с (из settings.json)");
        Transcript.SetDraftInterval(_settings.LiveDraftSeconds);
    }

    public Task StopRecordingAsync() => _recorder.StopAsync();

    /// <summary>The side tree selected a project (a new session in it) or a session (appending to it).</summary>
    public void Select(object? node)
    {
        if (_selecting || node is null)
        {
            return;
        }

        _selecting = true;
        switch (node)
        {
            case ProjectNode project:
                SelectedProject = project.Project;
                SelectedNode = null;
                SelectedSession = null;
                if (!IsRecording)
                {
                    SessionName = "";
                }

                Transcript.ShowSession(null);
                break;
            case SessionNode session:
                SelectedProject = session.Project.Project;
                SelectedNode = session;
                SelectedSession = session.Session;
                if (!IsRecording)
                {
                    SessionName = session.Name;
                }

                if (Transcript.ShownSession != session.Session)
                {
                    Transcript.ShowSession(session.Session);
                }

                break;
        }

        _selecting = false;
        RefreshAgents();
    }

    /// <summary>Hotkey: the next language of the project, round and round.</summary>
    public void ToggleLanguage()
    {
        if (Languages.Count > 1)
        {
            int index = Languages.ToList().FindIndex(l => l.Code == Language);
            Language = Languages[ (index + 1) % Languages.Count ].Code;
        }
    }

    /// <summary>Hotkey: starts or stops recording.</summary>
    public void ToggleRecording()
    {
        if (IsRecording)
        {
            StopCommand.Execute(null);
        }
        else if (StartCommand.CanExecute(null))
        {
            StartCommand.Execute(null);
        }
        else if (!IsStarting)
        {
            ShowMessage("Запись по хоткею не началась: сначала выберите проект и микрофон.", isError: true);
        }
    }

    /// <summary>
    /// Button and hotkey: the thought is finished — the phrase being spoken goes to the text at once, and the agents of
    /// the recording get everything they have not read, without waiting for their pause or word count.
    /// </summary>
    public void SendToAgentsNow()
    {
        if (!IsRecording || _recordingNode is null)
        {
            ShowMessage("Отправлять агенту нечего: запись не идёт.", isError: true);
            return;
        }

        if (RecordingAgents == 0)
        {
            ShowMessage("Отправлять некому: к этой записи не подключён ни один агент.", isError: true);
            return;
        }

        SessionInfo session = _recordingNode.Session;
        Transcript.Pipeline.Push(session);
        Cues.Pushed(_logger);
        int waiting = _agentsState?.AgentsOf(session).Count(a => a.State == AgentState.Waiting) ?? 0;
        ShowMessage(
            (waiting, RecordingAgents) switch
            {
                (0, 1) => "Отправлено: агент сейчас не ждёт и получит текст, как только снова спросит.",
                (0, _) => "Отправлено: агенты сейчас не ждут и получат текст, как только снова спросят.",
                (_, 1) => "Отправлено агенту: последняя фраза распознаётся, и он сразу получит весь текст.",
                _ => "Отправлено агентам: последняя фраза распознаётся, и они сразу получат весь текст.",
            },
            isError: false);
    }

    public void ShowHotkeyProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            ShowMessage($"Хоткеи не работают:\n{string.Join("\n", problems)}\nИзмените их в {_settingsStore.FilePath} и перезапустите mictap.", isError: true);
        }
    }

    /// <summary>Creates a project from the dialog and selects it.</summary>
    public void CreateProject(ProjectSettings settings)
    {
        try
        {
            ProjectInfo project = Core.Projects.Create(RootFolder, settings);
            _logger.Information("Project {Project} created in {Folder}", project.Name, project.Folder);
            LoadProjects(select: project.Folder);
            ShowMessage($"Проект «{project.Name}» создан.", isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.Error(ex, "Creating project {Project} failed", settings.Name);
            ShowMessage($"Проект не создан: {ex.Message}", isError: true);
        }
    }

    /// <summary>Saves a project's settings from the dialog.</summary>
    public void SaveProject(ProjectInfo project, ProjectSettings settings)
    {
        try
        {
            ProjectInfo saved = Core.Projects.Save(project, settings);
            _logger.Information("Project {Project} settings saved", saved.Name);
            LoadProjects(select: saved.Folder);
            ShowMessage($"Настройки проекта «{saved.Name}» сохранены.", isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.Error(ex, "Saving project {Project} failed", settings.Name);
            ShowMessage($"Настройки не сохранены: {ex.Message}", isError: true);
        }
    }

    /// <summary>The name the rename dialog starts with.</summary>
    public string NameToRename(SessionNode? node) =>
        node is null || node == _recordingNode ? PendingName is { Length: > 0 } pending ? pending : node?.Name ?? "" : node.Name;

    /// <summary>
    /// A name typed in the rename dialog: a session folder is renamed at once; the recording session and the new
    /// session of the next recording keep it until their folder can take it.
    /// </summary>
    public async Task ApplyRenameAsync(SessionNode? node, string typed)
    {
        string name = SessionFolders.Sanitize(typed);
        if (node is null && typed.Trim().Length == 0)
        {
            SessionName = ""; // the new session is named by its start time again
            return;
        }

        if (name.Length == 0)
        {
            ShowMessage($"«{typed}» не годится как имя сессии: в нём нет допустимых символов.", isError: true);
            return;
        }

        if (node is null || node == _recordingNode)
        {
            SessionName = name;
            OnPropertyChanged(nameof(SessionHeader));
            return;
        }

        await RenameSessionAsync(node, name);
    }

    /// <summary>Moves a session into another project; its transcript, audio and agent cursors move with it.</summary>
    public async Task MoveAsync(SessionNode node, ProjectNode target)
    {
        if (node.Project == target)
        {
            return;
        }

        if (node == _recordingNode)
        {
            ShowMessage($"Сессия «{node.Name}» записывается — перенести её можно после остановки.", isError: true);
            return;
        }

        string folder = Path.Combine(target.Project.Folder, node.Name);
        for (int copy = 2; Directory.Exists(folder); copy++)
        {
            folder = Path.Combine(target.Project.Folder, $"{node.Name}_{copy}");
        }

        if (!await Transcript.WhenIdleAsync(TimeSpan.FromSeconds(60)))
        {
            ShowMessage($"Сессия «{node.Name}» не перенесена: распознавание ещё не закончило её текст.", isError: true);
            return;
        }

        SessionInfo before = node.Session;
        try
        {
            Directory.Move(before.Folder, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Moving session {Session} to {Project} failed", before.Name, target.Name);
            ShowMessage($"Сессия «{before.Name}» не перенесена: {ex.Message}", isError: true);
            return;
        }

        var after = new SessionInfo(Path.GetFileName(folder), folder);
        Transcript.SessionRenamed(before, after);
        _logger.Information("Session {Session} moved from {From} to {To}", before.Name, node.Project.Name, target.Name);
        LoadProjects(select: target.Project.Folder);
        if (Projects.SelectMany(p => p.Sessions).FirstOrDefault(s => s.Session == after) is { } moved)
        {
            moved.IsSelected = true;
            Select(moved);
        }

        ShowMessage($"Сессия «{after.Name}» перенесена в проект «{target.Name}».", isError: false);
    }

    /// <summary>Moves a session to the archive with its transcript, audio and agent cursors.</summary>
    public async Task DeleteSessionAsync(SessionNode node)
    {
        if (node == _recordingNode)
        {
            ShowMessage($"Сессия «{node.Name}» записывается — удалить её можно после остановки.", isError: true);
            return;
        }

        if (!await Transcript.WhenIdleAsync(TimeSpan.FromSeconds(60)))
        {
            ShowMessage($"Сессия «{node.Name}» не удалена: распознавание ещё не закончило её текст.", isError: true);
            return;
        }

        try
        {
            Archive.ArchiveSession(RootFolder, node.Project.Project, node.Session, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Archiving session {Session} failed", node.Name);
            ShowMessage($"Сессия «{node.Name}» не удалена: {ex.Message}", isError: true);
            return;
        }

        Transcript.SessionDeleted(node.Session);
        ForgetLastRecorded(node);
        _logger.Information("Session {Session} of {Project} moved to the archive", node.Name, node.Project.Name);
        LoadProjects(select: node.Project.Project.Folder);
        ShowMessage($"Сессия «{node.Name}» в архиве. {ArchiveNote}", isError: false);
    }

    /// <summary>Moves a project with all its sessions to the archive; an empty root gets a first project again.</summary>
    public async Task DeleteProjectAsync(ProjectNode node)
    {
        if (IsRecording || IsStarting)
        {
            ShowMessage($"Проект «{node.Name}» не удалён: идёт запись. Остановите её и повторите.", isError: true);
            return;
        }

        if (!await Transcript.WhenIdleAsync(TimeSpan.FromSeconds(60)))
        {
            ShowMessage($"Проект «{node.Name}» не удалён: распознавание ещё не закончило текст его сессии.", isError: true);
            return;
        }

        try
        {
            Archive.ArchiveProject(RootFolder, node.Project, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Archiving project {Project} failed", node.Name);
            ShowMessage($"Проект «{node.Name}» не удалён: {ex.Message}", isError: true);
            return;
        }

        foreach (SessionNode session in node.Sessions)
        {
            Transcript.SessionDeleted(session.Session);
            ForgetLastRecorded(session);
        }

        _logger.Information("Project {Project} with {Sessions} sessions moved to the archive", node.Name, node.Sessions.Count);
        LoadProjects(); // the selected project stays; the deleted one gives way to the first
        ShowMessage($"Проект «{node.Name}» в архиве. {ArchiveNote}", isError: false);
    }

    /// <summary>Puts an archived project or session back where it was and selects it.</summary>
    public void RestoreArchived(ArchiveRow row)
    {
        string restored;
        try
        {
            restored = Archive.Restore(RootFolder, row.Entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.Error(ex, "Restoring {Folder} failed", row.Entry.Folder);
            ShowMessage($"Не восстановлено: {ex.Message}", isError: true);
            return;
        }

        _logger.Information("{Kind} {Name} restored from the archive to {Folder}", row.Entry.Info.Kind, row.Entry.Info.Name, restored);
        bool session = row.Entry.Info.Kind == ArchivedKind.Session;
        LoadProjects(select: session ? Path.GetDirectoryName(restored) : restored);
        if (session && Projects.SelectMany(p => p.Sessions).FirstOrDefault(s => s.Session.Folder == restored) is { } node)
        {
            node.Project.IsExpanded = true;
            node.IsSelected = true;
            Select(node);
        }

        RefreshArchive();
        ShowMessage($"Восстановлено: {row.Title}.", isError: false);
    }

    public void AskDeleteForever(ArchiveRow row) => row.IsConfirmingDelete = true;

    public void CancelDeleteForever(ArchiveRow row) => row.IsConfirmingDelete = false;

    /// <summary>The user said yes twice: the entry is deleted for good, not to the Recycle Bin.</summary>
    public void DeleteForever(ArchiveRow row)
    {
        try
        {
            Archive.DeleteForever(row.Entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Deleting {Folder} for good failed", row.Entry.Folder);
            ShowMessage($"Не удалено: {ex.Message}", isError: true);
            return;
        }

        _logger.Information("{Kind} {Name} deleted from the archive for good", row.Entry.Info.Kind, row.Entry.Info.Name);
        RefreshArchive();
    }

    /// <summary>What the archive holds now, newest first, with when each goes for good.</summary>
    public void RefreshArchive()
    {
        ArchiveRows.Clear();
        IReadOnlyList<ArchiveEntry> entries;
        try
        {
            entries = Archive.List(RootFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.Error(ex, "Reading the archive of {Root} failed", RootFolder);
            ShowMessage($"Архив не прочитан: {ex.Message}", isError: true);
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        foreach (ArchiveEntry entry in entries)
        {
            ArchiveRows.Add(ArchiveRow.For(entry, ArchiveKeep, now));
        }
    }

    private TimeSpan? ArchiveKeep => _settings.ArchiveDays > 0 ? TimeSpan.FromDays(_settings.ArchiveDays) : null;

    private string ArchiveNote => _settings.ArchiveDays > 0
        ? $"Восстановить — «Архив» над проектами; через {_settings.ArchiveDays} дн. удалится насовсем."
        : "Восстановить — «Архив» над проектами.";

    /// <summary>Deletes for good what has stayed in the archive longer than the settings keep it.</summary>
    private void PurgeArchive()
    {
        try
        {
            foreach (ArchiveEntry entry in Archive.Purge(RootFolder, ArchiveKeep, DateTimeOffset.Now))
            {
                _logger.Information("{Kind} {Name} deleted for good: archived on {Deleted}", entry.Info.Kind, entry.Info.Name, entry.Info.DeletedAt);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.Error(ex, "Cleaning the archive of {Root} failed", RootFolder);
        }
    }

    /// <summary>Renames a session folder; its transcript, audio and agent cursors move with it.</summary>
    public async Task RenameSessionAsync(SessionNode node, string typed)
    {
        string name = SessionFolders.Sanitize(typed);
        if (name.Length == 0 || name == node.Name)
        {
            return;
        }

        string target = Path.Combine(node.Project.Project.Folder, name);
        if (Directory.Exists(target))
        {
            ShowMessage($"Сессия «{name}» уже есть в проекте «{node.Project.Name}».", isError: true);
            return;
        }

        if (!await Transcript.WhenIdleAsync(TimeSpan.FromSeconds(60)))
        {
            ShowMessage($"Сессия «{node.Name}» не переименована: распознавание ещё не закончило её текст.", isError: true);
            return;
        }

        SessionInfo before = node.Session;
        try
        {
            Directory.Move(before.Folder, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Renaming session {Session} to {Name} failed", before.Name, name);
            ShowMessage($"Сессия «{before.Name}» не переименована: {ex.Message}", isError: true);
            return;
        }

        var after = new SessionInfo(name, target);
        node.Session = after;
        Transcript.SessionRenamed(before, after);
        if (SelectedSession == before)
        {
            SelectedSession = after;
            SessionName = name;
        }

        _logger.Information("Session {Before} renamed to {After}", before.Name, name);
        ShowMessage($"Сессия переименована: {before.Name} → {name}.", isError: false);
    }

    /// <summary>
    /// The user pasted a Router AI key in the settings dialog: it goes to the Windows Credential Manager, never to a
    /// file or the log. Projects with Router AI use it from their next phrase on.
    /// </summary>
    public void SaveRouterAiKey(string key)
    {
        string trimmed = key.Trim();
        if (trimmed.Length == 0)
        {
            RouterAiKeyText = "Ключ пустой — вставьте его в поле.";
            return;
        }

        try
        {
            CloudKeys.Save(trimmed);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.Error(ex, "Saving the Router AI key failed");
            RouterAiKeyText = $"Ключ не сохранён: {ex.Message}";
            return;
        }

        _logger.Information("Router AI key saved to the Credential Manager");
        ShowRouterAiKey();
    }

    /// <summary>«Заменить»: the input shows under the saved key; «Отмена» or saving goes back.</summary>
    public void ReplaceRouterAiKey()
    {
        IsReplacingRouterAiKey = true;
        CanSaveRouterAiKey = false;
    }

    public void DeleteRouterAiKey()
    {
        try
        {
            CloudKeys.Delete();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.Error(ex, "Deleting the Router AI key failed");
            RouterAiKeyText = $"Ключ не удалён: {ex.Message}";
            return;
        }

        _logger.Information("Router AI key deleted from the Credential Manager");
        ShowRouterAiKey();
    }

    /// <summary>Reads whether a key is saved, for the settings dialog; only its first and last characters are shown.</summary>
    public void ShowRouterAiKey()
    {
        IsReplacingRouterAiKey = false;
        CanSaveRouterAiKey = false;
        try
        {
            string? key = CloudKeys.Read();
            HasRouterAiKey = key is { Length: > 0 };
            RouterAiKeyText = key is { Length: > 0 }
                ? $"Ключ сохранён: {Mask(key)}"
                : "Ключа нет. Создайте его в кабинете Router AI и вставьте сюда.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            HasRouterAiKey = false;
            RouterAiKeyText = $"Ключ не прочитан: {ex.Message}";
        }
    }

    private static string Mask(string key) => key.Length > 10 ? $"{key[ ..3 ]}…{key[ ^4.. ]}" : "…";

    public void DismissMessage()
    {
        Message = "";
        IsError = false;
    }

    private bool CanEditProject(ProjectNode? node) => CanEditSettings && (node?.Project ?? SelectedProject) is not null;

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void NewProject() => NewProjectRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private void EditProject(ProjectNode? node) => EditProjectRequested?.Invoke(this, node?.Project ?? SelectedProject!);

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private void NewSession(ProjectNode? node)
    {
        ProjectNode? project = node ?? Projects.FirstOrDefault(p => p.Project.Folder == SelectedProject?.Folder);
        if (project is not null)
        {
            project.IsSelected = true;
            Select(project);
        }
    }

    private bool CanRenameSession(SessionNode? node) => !IsStarting && (node is not null || SelectedProject is not null);

    [RelayCommand(CanExecute = nameof(CanRenameSession))]
    private void RenameSession(SessionNode? node) => RenameSessionRequested?.Invoke(this, node);

    private bool CanDeleteSession(SessionNode? node) =>
        !IsStarting && node is not null && node != _recordingNode && !Transcript.IsRetranscribing;

    [RelayCommand(CanExecute = nameof(CanDeleteSession))]
    private void DeleteSession(SessionNode? node) =>
        DeleteRequested?.Invoke(this, new DeleteRequest(
            "Удалить сессию?",
            $"Сессия «{node!.Name}» — текст, звук и записи агентов — уйдёт в архив mictap. {ArchiveNote}",
            () => DeleteSessionAsync(node)));

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private void DeleteProject(ProjectNode? node)
    {
        ProjectNode project = node ?? Projects.First(p => p.Project.Folder == SelectedProject!.Folder);
        DeleteRequested?.Invoke(this, new DeleteRequest(
            "Удалить проект?",
            project.Sessions.Count == 0
                ? $"Проект «{project.Name}» уйдёт в архив mictap. {ArchiveNote}"
                : $"Проект «{project.Name}» и все его сессии ({project.Sessions.Count}) — текст, звук и записи агентов — уйдут в "
                  + $"архив mictap. {ArchiveNote}",
            () => DeleteProjectAsync(project)));
    }

    [RelayCommand]
    private void OpenArchive()
    {
        PurgeArchive();
        RefreshArchive();
        ArchiveRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenSessionFolder(SessionNode? node)
    {
        if (node is not null && Directory.Exists(node.Session.Folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{node.Session.Folder}\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Папка проектов" };
        if (Directory.Exists(RootFolder))
        {
            dialog.InitialDirectory = RootFolder;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        RootFolder = dialog.FolderName;
        if (_agentsState is not null)
        {
            _agentsState.Root = dialog.FolderName;
        }

        Save(_settings with { RootFolder = dialog.FolderName });
        ShowMessage("Папка проектов выбрана.", isError: false);
        LoadProjects();
        RepairProjects();
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void RefreshDevices()
    {
        IReadOnlyList<AudioDevice> devices = WaveInAudioSource.ListDevices();
        _refreshingDevices = true;
        Devices.Clear();
        foreach (AudioDevice device in devices)
        {
            Devices.Add(device);
        }

        SelectedDevice = devices.FirstOrDefault(d => d.Name == _settings.DeviceName) ?? devices.FirstOrDefault();
        _refreshingDevices = false;

        if (SelectedDevice is null)
        {
            ShowMessage("Микрофоны не найдены. Подключите микрофон и нажмите «Обновить» в настройках.", isError: true);
        }
        else if (_settings.DeviceName is not null && SelectedDevice.Name != _settings.DeviceName)
        {
            ShowMessage($"Микрофон «{_settings.DeviceName}» не найден.\nВыбран «{SelectedDevice.Name}».", isError: true);
        }
    }

    /// <summary>The agents hub: its presence feeds the tree badges and the agents list, refreshed every second.</summary>
    public void UseHub(AgentsState state)
    {
        _agentsState = state;
        state.AgentsChanged += (_, _) => _dispatcher.InvokeAsync(RefreshAgents);
        _ = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshAgents(), _dispatcher);
    }

    public void HubStarted(int port)
    {
        HubText = $"127.0.0.1:{port}";
        HubWarning = false;
    }

    public void HubFailed(string reason)
    {
        HubText = $"выключен: {reason}";
        HubWarning = true;
    }

    partial void OnIsRecordingChanged(bool value)
    {
        // A sleeping laptop stops capture: block system sleep for the length of the recording.
        if (value ? SystemSleep.Block() : SystemSleep.Allow())
        {
            _logger.Information(value ? "System sleep blocked while recording" : "System sleep allowed again");
        }
        else
        {
            _logger.Error("Windows rejected the keep-awake change (recording: {Recording})", value);
        }
    }

    partial void OnLanguageChanged(string value)
    {
        if (_quietLanguage)
        {
            return;
        }

        // During a recording the switch is a mark: speech from here on is recognized in the new language.
        _recorder.MarkLanguage(value);
        Cues.Language(value, _logger);
    }

    partial void OnSelectedProjectChanged(ProjectInfo? value)
    {
        _quietLanguage = true;
        Languages.Clear();
        foreach (string code in value?.Settings.Languages ?? [])
        {
            Languages.Add(new LanguageOption(code, LanguageNames.GetValueOrDefault(code, code.ToUpperInvariant())));
        }

        Language = value?.Settings.DefaultLanguage ?? "ru";
        _quietLanguage = false;
        RefreshEngineChoices();
    }

    partial void OnSelectedArchiveChanged(ArchiveOption? value)
    {
        if (value is null || value.Days == _settings.ArchiveDays)
        {
            return;
        }

        Save(_settings with { ArchiveDays = value.Days });
        _logger.Information("Archive keeps {Days}", value.Days == 0 ? "everything" : $"{value.Days} days");
        PurgeArchive();
    }

    partial void OnSelectedDraftChanged(DraftOption? value)
    {
        if (value is null || value.Seconds == _settings.LiveDraftSeconds)
        {
            return;
        }

        Save(_settings with { LiveDraftSeconds = value.Seconds });
        Transcript.SetDraftInterval(value.Seconds);
        _logger.Information("Live draft {Draft}", value.Seconds == 0 ? "off" : $"every {value.Seconds} s");
    }

    partial void OnSelectedEngineChanged(EngineChoice? value)
    {
        if (_quietEngine || value is null || SelectedProject is not { } project || project.Settings.CloudModelInUse == value.Model)
        {
            return;
        }

        ProjectSettings settings = project.Settings with
        {
            Engine = value.Model is null ? TranscriptionEngine.Local : TranscriptionEngine.RouterAi,
            CloudModel = value.Model ?? project.Settings.CloudModel,
        };
        try
        {
            ProjectInfo saved = Core.Projects.Save(project, settings);
            _logger.Information("Project {Project} phrases now go to {Engine}", saved.Name, value.Model ?? "the local Whisper");
            LoadProjects(select: saved.Folder);
            bool recordingHere = _recordingNode is { } recording && recording.Project.Project.Folder == saved.Folder;
            Transcript.EngineSwitched(recordingHere ? _recordingNode!.Session : null, value.Model);
            ShowMessage(
                (value.Model is null
                    ? $"Проект «{saved.Name}»: фразы распознаёт видеокарта."
                    : $"Проект «{saved.Name}»: фразы распознаёт Router AI · {value.Model} ({value.Price}).")
                + " Со следующей фразы, и для тех, что ждут в очереди.",
                isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.Error(ex, "Switching the engine of {Project} failed", project.Name);
            ShowMessage($"Распознавание не переключено: {ex.Message}", isError: true);
            RefreshEngineChoices();
        }
    }

    partial void OnSelectedDeviceChanged(AudioDevice? value)
    {
        if (!_refreshingDevices && value is not null && value.Name != _settings.DeviceName)
        {
            Save(_settings with { DeviceName = value.Name });
        }
    }

    private bool CanStart() => !IsRecording && !IsStarting && SelectedProject is not null && SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ProjectInfo project = SelectedProject!;
        string typed = SessionName.Trim();
        string name = typed.Length == 0 ? SessionFolders.DefaultName(DateTimeOffset.Now) : SessionFolders.Sanitize(typed);
        if (name.Length == 0)
        {
            ShowMessage($"«{typed}» не годится как имя сессии: в нём нет допустимых символов.", isError: true);
            return;
        }

        string device = SelectedDevice!.Name;
        SessionInfo session;
        IsStarting = true;
        try
        {
            session = SessionFolders.Open(project.Folder, name);
            var options = new RecordingOptions(
                session, device, _settings.ChunkSeconds, _appVersion, Language, project.Settings.AudioRetention, project.Settings.PhrasePause,
                project.Settings.CloudModelInUse);

            // Opening a capture device can take seconds; the window stays responsive meanwhile.
            session = await Task.Run(() => _recorder.Start(options, WaveInAudioSource.Open(device)));
        }
        catch (AudioCaptureException ex)
        {
            _logger.Error(ex, "Recording did not start");
            ShowMessage($"Запись не началась: {Describe(ex)}", isError: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error(ex, "Recording did not start");
            ShowMessage($"Запись не началась: {ex.Message}", isError: true);
            return;
        }
        finally
        {
            IsStarting = false;
        }

        if (!_recorder.IsRecording)
        {
            return; // the device failed right after starting; the Stopped event reports why
        }

        ProjectNode projectNode = Projects.First(p => p.Project.Folder == project.Folder);
        SessionNode node = projectNode.Sessions.FirstOrDefault(s => s.Session == session) ?? new SessionNode(session, projectNode);
        if (!projectNode.Sessions.Contains(node))
        {
            projectNode.Sessions.Insert(0, node);
        }

        _recordingNode = node;
        node.IsRecording = true;
        Transcript.BeginSession(session, project.Settings.CloudModelInUse);
        IsRecording = true;
        SessionText = session.Name;
        SessionName = session.Name;
        projectNode.IsExpanded = true;
        _selecting = true;
        node.IsSelected = true;
        SelectedNode = node;
        SelectedSession = session;
        _selecting = false;
        ChunkText = project.Settings.AudioRetention == AudioRetention.None ? "не пишется" : "—";
        ShowRecorded(TimeSpan.Zero);
        RenameSessionCommand.NotifyCanExecuteChanged();
        ShowMessage($"Идёт запись: {project.Name} · {session.Name}.", isError: false);

        if (_settings.DeviceName != device)
        {
            Save(_settings with { DeviceName = device });
        }
    }

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private Task StopAsync() => _recorder.StopAsync();

    [RelayCommand(CanExecute = nameof(HasRecordingAgents))]
    private void SendToAgents() => SendToAgentsNow();

    private bool CanRetranscribe(SessionNode? node) =>
        !IsRecording && !IsStarting && (node?.Session ?? SelectedSession) is { } session && !Transcript.IsRetranscribing
        && Directory.Exists(session.AudioFolder) && Directory.EnumerateFiles(session.AudioFolder, "*.wav").Any();

    [RelayCommand(CanExecute = nameof(CanRetranscribe))]
    private Task RetranscribeAsync(SessionNode? node)
    {
        SessionInfo session = node?.Session ?? SelectedSession!;
        ProjectInfo project = node?.Project.Project ?? SelectedProject!;
        return Transcript.RetranscribeAsync(session, project.Settings.DefaultLanguage, project.Settings.PhrasePause);
    }

    /// <summary>The speech-to-text models of Router AI with prices, from its public catalog; offline, the project's own model.</summary>
    private async Task LoadCloudModelsAsync()
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            _cloudModels = await RouterAiModels.LoadAsync(http, CancellationToken.None);
            _logger.Information("Router AI catalog: {Count} speech-to-text models", _cloudModels.Count);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            or KeyNotFoundException or InvalidOperationException)
        {
            _logger.Warning(ex, "Router AI catalog not loaded; the engine box offers the projects' own models only");
            return;
        }

        RefreshEngineChoices();
    }

    /// <summary>The engine box for the selected project: the graphics card, the catalog's models and the project's own model.</summary>
    private void RefreshEngineChoices()
    {
        _quietEngine = true;
        EngineChoices.Clear();
        EngineChoices.Add(new EngineChoice(null, "Видеокарта · Whisper", "бесплатно"));
        string? current = SelectedProject?.Settings.CloudModelInUse;
        foreach (CloudModelInfo model in _cloudModels)
        {
            EngineChoices.Add(new EngineChoice(model.Id, model.Id, PriceText(model.RublesPerMinute)));
        }

        foreach (string model in new[] { current, SelectedProject?.Settings.CloudModel, RouterAiTranscriber.DefaultModel }.OfType<string>().Distinct())
        {
            if (EngineChoices.All(choice => choice.Model != model))
            {
                EngineChoices.Insert(1, new EngineChoice(model, model, "цена неизвестна"));
            }
        }

        SelectedEngine = EngineChoices.First(choice => choice.Model == current);
        _quietEngine = false;
    }

    private static string PriceText(double? rublesPerMinute) =>
        rublesPerMinute is { } price ? $"{price:0.###} ₽/мин" : "по токенам";

    /// <summary>Reads the projects of the root; an empty root gets a first project so recording works at once.</summary>
    private void LoadProjects(string? select = null)
    {
        IReadOnlyList<ProjectInfo> projects;
        try
        {
            projects = Core.Projects.List(RootFolder);
            if (projects.Count == 0)
            {
                ProjectInfo first = Core.Projects.Create(RootFolder, Core.Projects.FirstProject);
                _logger.Information("First project {Project} created in {Folder}", first.Name, first.Folder);
                projects = [ first ];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error(ex, "Reading projects in {Root} failed", RootFolder);
            ShowMessage($"Не удалось прочитать проекты в {RootFolder}:\n{ex.Message}", isError: true);
            projects = [];
        }

        string? keep = select ?? SelectedProject?.Folder;
        HashSet<string> unfolded = [ .. Projects.Where(p => p.IsExpanded).Select(p => p.Project.Folder) ];
        Projects.Clear();
        foreach (ProjectInfo project in projects)
        {
            var node = new ProjectNode(project) { IsExpanded = unfolded.Contains(project.Folder) };
            foreach (SessionInfo session in SessionFolders.List(project.Folder))
            {
                node.Sessions.Add(new SessionNode(session, node));
            }

            Projects.Add(node);
        }

        // A rebuilt tree while recording: the recording session's new row carries on as the recording one.
        if (_recordingNode is { } recording
            && Projects.SelectMany(p => p.Sessions).FirstOrDefault(s => s.Session == recording.Session) is { } again)
        {
            again.IsRecording = true;
            _recordingNode = again;
        }

        ProjectNode? selected = Projects.FirstOrDefault(p => p.Project.Folder == keep) ?? Projects.FirstOrDefault();
        if (selected is not null)
        {
            selected.IsExpanded = true;
            selected.IsSelected = true;
            Select(selected);
        }
    }

    private void RepairProjects()
    {
        IReadOnlyList<RepairReport> reports;
        try
        {
            reports = SessionRepair.RepairProjects(RootFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error(ex, "Checking the latest sessions in {Root} failed", RootFolder);
            ShowMessage($"Не удалось проверить последние сессии в {RootFolder}: {ex.Message}", isError: true);
            return;
        }

        foreach (RepairReport report in reports)
        {
            foreach (string chunk in report.Repaired)
            {
                _logger.Warning("Chunk left open by a crash repaired: {Chunk}", chunk);
            }

            foreach (string chunk in report.Unrecognized)
            {
                _logger.Warning("Chunk with an unrecognized header left as is: {Chunk}", chunk);
            }
        }

        if (reports.FirstOrDefault(r => r.Unrecognized.Count > 0) is { } broken)
        {
            ShowMessage(
                $"В сессии {broken.Session.Name} есть файлы с нераспознанным заголовком, они не изменены:\n{FileNames(broken.Unrecognized)}.",
                isError: true);
        }
        else if (reports.FirstOrDefault(r => r.Repaired.Count > 0) is { } repaired)
        {
            ShowMessage(
                $"Сессия {repaired.Session.Name} была прервана сбоем.\nЗаголовки восстановлены: {FileNames(repaired.Repaired)}.",
                isError: false);
        }
    }

    private void RefreshAgents()
    {
        if (_agentsState is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int connected = 0;
        foreach (ProjectNode project in Projects)
        {
            int inProject = 0;
            foreach (SessionNode session in project.Sessions)
            {
                IReadOnlyList<AgentPresence> agents = _agentsState.AgentsOf(session.Session);
                AgentPresence[] present = [ .. agents.Where(a => a.State != AgentState.Gone) ];
                session.AgentCount = present.Length;
                session.AgentsText = string.Join("\n", present.Select(a => $"{a.Name} · {Describe(a, now)}"));
                inProject += present.Length;
                connected += agents.Count(a => a.State == AgentState.Waiting);
            }

            project.AgentCount = inProject;
        }

        RecordingAgents = _recordingNode is null ? 0 : _agentsState.AgentsOf(_recordingNode.Session).Count(a => a.State != AgentState.Gone);

        AgentRow[] rows = SelectedSession is null
            ? []
            : [ .. _agentsState.AgentsOf(SelectedSession).Select(a => new AgentRow(a.Name, Describe(a, now))) ];
        if (!rows.SequenceEqual(Agents))
        {
            Agents.Clear();
            foreach (AgentRow row in rows)
            {
                Agents.Add(row);
            }
        }

        if (!HubWarning && HubText.StartsWith("127.", StringComparison.Ordinal))
        {
            string address = HubText.Split(' ')[ 0 ];
            HubText = connected > 0 ? $"{address} · агентов: {connected}" : address;
        }
    }

    private void OnLevelChanged(object? sender, double peak) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (IsRecording)
            {
                // Rise at once, fall back smoothly: a raw 10 Hz peak makes the meter flicker.
                Level = Math.Max(AudioLevel.ToMeter(peak) * 100, Level * 0.8);
            }
        });

    private void OnProgressChanged(object? sender, RecordingProgress progress) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (IsRecording)
            {
                if (progress.ChunkIndex >= 0)
                {
                    ChunkText = ChunkedWavWriter.FileName(progress.ChunkIndex);
                }

                ShowRecorded(progress.Recorded);
            }
        });

    private void OnRecordingStopped(object? sender, RecordingStopped stopped) =>
        _dispatcher.InvokeAsync(async () =>
        {
            SessionNode? node = _recordingNode;
            _recordingNode = null;
            if (node is not null)
            {
                node.IsRecording = false;
            }

            IsRecording = false;
            Level = 0;
            Transcript.EndSession();
            ShowRecorded(stopped.Recorded);
            RetranscribeCommand.NotifyCanExecuteChanged();
            RenameSessionCommand.NotifyCanExecuteChanged();
            if (stopped.Reason == StopReason.Requested)
            {
                ShowMessage($"Запись остановлена.\nСессия {stopped.Session.Name} сохранена.", isError: false);
            }
            else
            {
                string text = $"Запись остановлена: {Describe(stopped)}\nЗаписанное сохранено в сессии {stopped.Session.Name}.";
                ShowMessage(text, isError: true);
                RecordingFailed?.Invoke(this, text);
            }

            // A name typed during the recording goes to the folder now that nothing is written there.
            if (node is not null && SessionName.Trim().Length > 0 && SessionFolders.Sanitize(SessionName) != node.Name)
            {
                await RenameSessionAsync(node, SessionName);
            }
        });

    /// <summary>The status line names the last recorded session: not once it is in the Recycle Bin.</summary>
    private void ForgetLastRecorded(SessionNode deleted)
    {
        if (_recordingNode == deleted)
        {
            _recordingNode = null;
            SessionText = "—";
        }
    }

    private void Save(AppSettings settings)
    {
        _settingsStore.Save(settings);
        _settings = settings;
    }

    private void ShowMessage(string text, bool isError)
    {
        Message = text;
        IsError = isError;
        _infoTimer.Stop();
        if (!isError)
        {
            _infoTimer.Start();
        }
    }

    private void ClearInfo()
    {
        _infoTimer.Stop();
        if (!IsError)
        {
            Message = "";
        }
    }

    /// <summary>«Записано» counts this recording; the timer shows session time, as the phrases do.</summary>
    private void ShowRecorded(TimeSpan recorded)
    {
        RecordedText = FormatMinutes(recorded);
        ElapsedText = FormatElapsed(TimeSpan.FromTicks(Interlocked.Read(ref _sessionOffsetTicks)) + recorded);
    }

    private static string Describe(AgentPresence agent, DateTimeOffset now) => agent.State switch
    {
        AgentState.Waiting => "ждёт",
        AgentState.Thinking => $"думает · {(int)(now - agent.LastSeen).TotalSeconds} с",
        _ => "ушёл",
    };

    private static string Describe(RecordingStopped stopped) => stopped.Reason switch
    {
        StopReason.DeviceFailed => stopped.Error is null
            ? "микрофон прекратил захват."
            : $"ошибка микрофона ({stopped.Error.Message}).",
        StopReason.DeviceSilent => $"микрофон не передаёт звук {Recorder.SilenceTimeout.TotalSeconds:0} с — вероятно, он отключён.",
        StopReason.WriteFailed => $"ошибка записи на диск ({stopped.Error?.Message}).",
        StopReason.WriteStalled => $"диск не успевает: больше {Recorder.DefaultWriteBuffer.TotalSeconds:0} с звука ждали записи. "
            + "Проверьте, не занят ли диск (антивирус, синхронизация OneDrive) или выберите другую папку.",
        _ => "",
    };

    private static string Describe(AudioCaptureException error) => error.Failure switch
    {
        CaptureFailure.DeviceNotFound => $"микрофон «{error.DeviceName}» не найден. Подключите его и нажмите «Обновить» в настройках.",
        _ => $"микрофон «{error.DeviceName}» не запустился в формате 16 кГц / моно / 16 бит ({error.InnerException?.Message}).",
    };

    private static string FileNames(IEnumerable<string> paths) => string.Join(", ", paths.Select(Path.GetFileName));

    private static string FormatMinutes(TimeSpan recorded) => $"{recorded.TotalMinutes:0.0} мин";

    private static string FormatElapsed(TimeSpan recorded) =>
        $"{(int)recorded.TotalHours:00}:{recorded.Minutes:00}:{recorded.Seconds:00}";
}

public sealed record LanguageOption(string Code, string Name);

/// <summary>How long the archive keeps a deleted project or session; <paramref name="Days"/> 0 is forever.</summary>
public sealed record ArchiveOption(int Days, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A line of the archive dialog; <see cref="IsConfirmingDelete"/> asks once more before deleting for good.</summary>
public sealed partial class ArchiveRow(ArchiveEntry entry, string title, string details) : ObservableObject
{
    [ObservableProperty]
    private bool _isConfirmingDelete;

    public ArchiveEntry Entry { get; } = entry;

    public string Title { get; } = title;

    public string Details { get; } = details;

    public static ArchiveRow For(ArchiveEntry entry, TimeSpan? keep, DateTimeOffset now)
    {
        ArchiveInfo info = entry.Info;
        string title = info.Kind == ArchivedKind.Project ? $"Проект «{info.Name}»" : $"Сессия «{info.Name}» · проект «{info.ProjectName}»";
        string deleted = $"удалено {info.DeletedAt.ToLocalTime():dd.MM HH:mm}";
        string expires = entry.ExpiresAt(keep) is { } at
            ? $" · насовсем через {Math.Max(0, (int)Math.Ceiling((at - now).TotalDays))} дн."
            : "";
        return new ArchiveRow(entry, title, deleted + expires);
    }
}

/// <summary>How often the draft is recognized; <paramref name="Seconds"/> 0 is off.</summary>
public sealed record DraftOption(int Seconds, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An item of the engine box: <paramref name="Model"/> null is the local Whisper on the graphics card.</summary>
public sealed record EngineChoice(string? Model, string Title, string Price)
{
    public override string ToString() => $"{Title} — {Price}";
}

/// <summary>An agent of the shown session and what it is doing, in words.</summary>
public sealed record AgentRow(string Name, string State);

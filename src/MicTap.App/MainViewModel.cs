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
    private bool _selecting;
    private AgentsState? _agentsState;
    private SessionNode? _recordingNode;

    [ObservableProperty]
    private string _rootFolder = Core.Projects.DefaultRoot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDevice? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings), nameof(IsIdle), nameof(TrayToolTip), nameof(EmptyText), nameof(SessionNote), nameof(SessionHeader))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(StopCommand), nameof(ChooseFolderCommand), nameof(RefreshDevicesCommand),
        nameof(RetranscribeCommand), nameof(NewProjectCommand), nameof(EditProjectCommand), nameof(NewSessionCommand),
        nameof(RenameSessionCommand), nameof(SendToAgentsCommand))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(ChooseFolderCommand), nameof(RefreshDevicesCommand), nameof(RetranscribeCommand),
        nameof(NewProjectCommand), nameof(EditProjectCommand), nameof(NewSessionCommand), nameof(RenameSessionCommand))]
    private bool _isStarting;

    [ObservableProperty]
    private double _level;

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
        nameof(RenameSessionCommand))]
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
        transcript.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptViewModel.IsRetranscribing))
            {
                RetranscribeCommand.NotifyCanExecuteChanged();
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

    /// <summary>The window shows the settings dialog.</summary>
    public event EventHandler? SettingsRequested;

    public ObservableCollection<AudioDevice> Devices { get; } = [];

    /// <summary>Projects with their sessions, for the side tree.</summary>
    public ObservableCollection<ProjectNode> Projects { get; } = [];

    /// <summary>Languages of the selected project; the first one is its default.</summary>
    public ObservableCollection<LanguageOption> Languages { get; } = [];

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
        $"Нажмите запись или {Hotkeys.ToggleRecording} — здесь появится то, что вы говорите. {Hotkeys.ToggleLanguage} — следующий язык.";

    public string SendToAgentsToolTip =>
        $"Отправить агенту сейчас — {Hotkeys.ForcePush}, во время записи, и из трея. Фраза, которую вы договорили, сразу уходит в текст, " +
        "а агент получает всё, что ещё не читал, и отвечает, не дожидаясь паузы и нужного числа слов.";

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

        SessionInfo session = _recordingNode.Session;
        Transcript.Pipeline.Push(session);
        Cues.Pushed(_logger);
        int waiting = _agentsState?.AgentsOf(session).Count(a => a.State == AgentState.Waiting) ?? 0;
        ShowMessage(
            waiting > 0
                ? "Отправлено агенту: последняя фраза распознаётся, и он сразу получит весь текст."
                : "Отправлено: агент сейчас не ждёт, получит текст сразу, как только снова спросит.",
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
                session, device, _settings.ChunkSeconds, _appVersion, Language, project.Settings.AudioRetention, project.Settings.PhrasePause);

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
        Transcript.BeginSession(session);
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

    [RelayCommand(CanExecute = nameof(IsRecording))]
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

    private void ShowRecorded(TimeSpan recorded)
    {
        RecordedText = FormatMinutes(recorded);
        ElapsedText = FormatElapsed(recorded);
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

/// <summary>An agent of the shown session and what it is doing, in words.</summary>
public sealed record AgentRow(string Name, string State);

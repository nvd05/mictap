using System.Collections.ObjectModel;
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
    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["ru"] = "Русский",
        ["ja"] = "日本語",
        ["en"] = "English",
        ["de"] = "Deutsch",
        ["fr"] = "Français",
        ["es"] = "Español",
        ["it"] = "Italiano",
        ["uk"] = "Українська",
        ["zh"] = "中文",
        ["ko"] = "한국어",
    };

    private readonly SettingsStore _settingsStore;
    private readonly Recorder _recorder;
    private readonly string _appVersion;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private AppSettings _settings;
    private bool _refreshingDevices;
    private bool _quietLanguage = true; // no tone and no mark while the language follows the selected project
    private bool _followRecording;
    private AgentsState? _agentsState;

    [ObservableProperty]
    private string _rootFolder = Core.Projects.DefaultRoot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDevice? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings), nameof(IsIdle), nameof(RecordButtonText), nameof(TrayToolTip))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(StopCommand), nameof(ChooseFolderCommand), nameof(RefreshDevicesCommand),
        nameof(RetranscribeCommand), nameof(NewProjectCommand), nameof(EditProjectCommand), nameof(NewSessionCommand))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings), nameof(RecordButtonText))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(ChooseFolderCommand), nameof(RefreshDevicesCommand), nameof(RetranscribeCommand),
        nameof(NewProjectCommand), nameof(EditProjectCommand), nameof(NewSessionCommand))]
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
    [NotifyPropertyChangedFor(nameof(Title), nameof(RetentionText), nameof(TrayToolTip), nameof(RecordButtonText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(EditProjectCommand), nameof(NewSessionCommand), nameof(RetranscribeCommand))]
    private ProjectInfo? _selectedProject;

    /// <summary>The session shown in «Записано» and recognized again by «Распознать заново».</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetranscribeCommand))]
    private SessionInfo? _selectedSession;

    /// <summary>The session the next recording goes to; the name of an existing one means appending to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(SessionHint))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
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

    public ObservableCollection<AudioDevice> Devices { get; } = [];

    public ObservableCollection<ProjectInfo> Projects { get; } = [];

    public ObservableCollection<SessionInfo> Sessions { get; } = [];

    /// <summary>Languages of the selected project; the first one is its default.</summary>
    public ObservableCollection<LanguageOption> Languages { get; } = [];

    public TranscriptViewModel Transcript { get; }

    /// <summary>Agents of the shown session: waiting, thinking or gone.</summary>
    public ObservableCollection<AgentRow> Agents { get; } = [];

    public bool CanEditSettings => !IsRecording && !IsStarting;

    public bool IsIdle => !IsRecording;

    public string RecordButtonText => IsRecording ? "Остановить запись"
        : IsStarting ? "Запуск…"
        : SelectedProject is null ? "Сначала создайте проект"
        : "Начать запись";

    public string Title => SelectedProject is null ? "" : $"{SelectedProject.Name} · {SessionName}";

    public string SessionHint =>
        SelectedProject is not null && SessionFolders.Sanitize(SessionName) is { Length: > 0 } name
            && File.Exists(new SessionInfo(name, Path.Combine(SelectedProject.Folder, name)).MetaFile)
            ? "Такая сессия есть: запись допишется в неё."
            : "Новая сессия.";

    public string RetentionText => SelectedProject?.Settings.AudioRetention switch
    {
        AudioRetention.Keep => "Звук хранится в audio\\.",
        AudioRetention.DeleteAfterTranscript => "Звук удаляется, как только фраза записана в текст.",
        AudioRetention.None => "Звук не сохраняется: «Распознать заново» недоступно.",
        _ => "",
    };

    public string LanguageText => Language.ToUpperInvariant();

    public HotkeySettings Hotkeys => _settings.Hotkeys;

    public string TrayToolTip => IsRecording
        ? $"mictap: идёт запись, {RecordedText}, язык {LanguageText}"
        : $"mictap: запись не идёт, язык {LanguageText}";

    /// <summary>Lists microphones and projects and repairs chunks that a crash left open.</summary>
    public void Initialize()
    {
        RefreshDevices();
        LoadProjects();
        RepairProjects();
    }

    public Task StopRecordingAsync() => _recorder.StopAsync();

    /// <summary>The agents hub: its presence feeds the agents list, refreshed every second for «думает · N с».</summary>
    public void UseHub(AgentsState state)
    {
        _agentsState = state;
        state.AgentsChanged += (_, _) => _dispatcher.InvokeAsync(RefreshAgents);
        _ = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshAgents(), _dispatcher); // runs while the dispatcher lives
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

    /// <summary>Hotkey: the next language of the project, round and round.</summary>
    public void ToggleLanguage()
    {
        if (Languages.Count > 1)
        {
            int index = Languages.ToList().FindIndex(l => l.Code == Language);
            Language = Languages[(index + 1) % Languages.Count].Code;
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

    /// <summary>Saves the selected project's settings from the dialog.</summary>
    public void SaveProject(ProjectSettings settings)
    {
        try
        {
            ProjectInfo saved = Core.Projects.Save(SelectedProject!, settings);
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

    private bool CanEditProject() => CanEditSettings && SelectedProject is not null;

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void NewProject() => NewProjectRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private void EditProject() => EditProjectRequested?.Invoke(this, SelectedProject!);

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private void NewSession()
    {
        SelectedSession = null;
        SessionName = SessionFolders.DefaultName(DateTimeOffset.Now);
    }

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
            ShowMessage("Микрофоны не найдены. Подключите микрофон и нажмите «Обновить».", isError: true);
        }
        else if (_settings.DeviceName is not null && SelectedDevice.Name != _settings.DeviceName)
        {
            ShowMessage($"Микрофон «{_settings.DeviceName}» не найден.\nВыбран «{SelectedDevice.Name}».", isError: true);
        }
        else
        {
            ShowMessage($"Найдено микрофонов: {devices.Count}.", isError: false);
        }
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
        LanguageCue.Play(value, _logger);
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
        LoadSessions();
    }

    partial void OnSelectedSessionChanged(SessionInfo? value)
    {
        if (_followRecording)
        {
            return; // the recording selected its own session; the transcript already shows it
        }

        if (value is not null)
        {
            SessionName = value.Name;
        }

        Transcript.ShowSession(value);
        RefreshAgents();
    }

    partial void OnSelectedDeviceChanged(AudioDevice? value)
    {
        if (!_refreshingDevices && value is not null && value.Name != _settings.DeviceName)
        {
            Save(_settings with { DeviceName = value.Name });
        }
    }

    private bool CanStart() =>
        !IsRecording && !IsStarting && SelectedProject is not null && SelectedDevice is not null && SessionName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ProjectInfo project = SelectedProject!;
        string name = SessionFolders.Sanitize(SessionName);
        if (name.Length == 0)
        {
            ShowMessage($"«{SessionName}» не годится как имя сессии: в нём нет допустимых символов.", isError: true);
            return;
        }

        string device = SelectedDevice!.Name;
        SessionInfo session;
        IsStarting = true;
        try
        {
            session = SessionFolders.Open(project.Folder, name);
            var options = new RecordingOptions(
                session, device, _settings.ChunkSeconds, _appVersion, Language, project.Settings.AudioRetention);

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

        IsRecording = true;
        SessionText = session.Name;
        SessionName = session.Name;
        Transcript.BeginSession(session);
        _followRecording = true;
        if (!Sessions.Contains(session))
        {
            Sessions.Insert(0, session);
        }

        SelectedSession = session;
        _followRecording = false;
        ChunkText = project.Settings.AudioRetention == AudioRetention.None ? "не пишется" : "—";
        ShowRecorded(TimeSpan.Zero);
        ShowMessage($"Идёт запись: {project.Name} · {session.Name}.", isError: false);

        if (_settings.DeviceName != device)
        {
            Save(_settings with { DeviceName = device });
        }
    }

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private Task StopAsync() => _recorder.StopAsync();

    private bool CanRetranscribe() =>
        !IsRecording && !IsStarting && SelectedSession is not null && SelectedProject is not null && !Transcript.IsRetranscribing
        && Directory.Exists(SelectedSession.AudioFolder) && Directory.EnumerateFiles(SelectedSession.AudioFolder, "*.wav").Any();

    [RelayCommand(CanExecute = nameof(CanRetranscribe))]
    private Task RetranscribeAsync() => Transcript.RetranscribeAsync(SelectedSession!, SelectedProject!.Settings.DefaultLanguage);

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
                projects = [first];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error(ex, "Reading projects in {Root} failed", RootFolder);
            ShowMessage($"Не удалось прочитать проекты в {RootFolder}:\n{ex.Message}", isError: true);
            projects = [];
        }

        string? keep = select ?? SelectedProject?.Folder;
        Projects.Clear();
        foreach (ProjectInfo project in projects)
        {
            Projects.Add(project);
        }

        SelectedProject = projects.FirstOrDefault(p => p.Folder == keep) ?? projects.FirstOrDefault();
    }

    private void LoadSessions()
    {
        Sessions.Clear();
        foreach (SessionInfo session in SelectedProject is null ? [] : SessionFolders.List(SelectedProject.Folder))
        {
            Sessions.Add(session);
        }

        SelectedSession = null;
        SessionName = SessionFolders.DefaultName(DateTimeOffset.Now);
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
        _dispatcher.InvokeAsync(() =>
        {
            IsRecording = false;
            Level = 0;
            Transcript.EndSession();
            ShowRecorded(stopped.Recorded);
            OnPropertyChanged(nameof(SessionHint));
            RetranscribeCommand.NotifyCanExecuteChanged();
            if (stopped.Reason == StopReason.Requested)
            {
                ShowMessage($"Запись остановлена.\nСессия {stopped.Session.Name} сохранена.", isError: false);
                return;
            }

            string text = $"Запись остановлена: {Describe(stopped)}\nЗаписанное сохранено в сессии {stopped.Session.Name}.";
            ShowMessage(text, isError: true);
            RecordingFailed?.Invoke(this, text);
        });

    private void RefreshAgents()
    {
        IReadOnlyList<AgentPresence> agents = _agentsState is null || SelectedSession is null ? [] : _agentsState.AgentsOf(SelectedSession);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AgentRow[] rows = [.. agents.Select(a => new AgentRow(a.Name, a.State switch
        {
            AgentState.Waiting => "ждёт",
            AgentState.Thinking => $"думает · {(int)(now - a.LastSeen).TotalSeconds} с",
            _ => "ушёл",
        }))];
        if (!rows.SequenceEqual(Agents))
        {
            Agents.Clear();
            foreach (AgentRow row in rows)
            {
                Agents.Add(row);
            }
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
    }

    private void ShowRecorded(TimeSpan recorded)
    {
        RecordedText = FormatMinutes(recorded);
        ElapsedText = FormatElapsed(recorded);
    }

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
        CaptureFailure.DeviceNotFound => $"микрофон «{error.DeviceName}» не найден. Подключите его и нажмите «Обновить».",
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

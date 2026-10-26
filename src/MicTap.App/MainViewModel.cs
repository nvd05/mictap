using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicTap.Core;
using Microsoft.Win32;
using Serilog;

namespace MicTap.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore;
    private readonly Recorder _recorder;
    private readonly string _appVersion;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private AppSettings _settings;
    private bool _refreshingDevices;
    private bool _ready;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _rootFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDevice? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings), nameof(IsIdle), nameof(RecordButtonText), nameof(TrayToolTip))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ChooseFolderCommand), nameof(RefreshDevicesCommand))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings), nameof(RecordButtonText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(ChooseFolderCommand), nameof(RefreshDevicesCommand))]
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
    [NotifyPropertyChangedFor(nameof(LanguageText), nameof(TrayToolTip))]
    private string _language = AppSettings.DefaultLanguage;

    public MainViewModel(
        SettingsStore settingsStore, AppSettings settings, Recorder recorder, TranscriptViewModel transcript, string appVersion, ILogger logger)
    {
        Transcript = transcript;
        _settingsStore = settingsStore;
        _settings = settings;
        _recorder = recorder;
        _appVersion = appVersion;
        _logger = logger.ForContext<MainViewModel>();
        RootFolder = settings.RootFolder;
        Language = settings.Language;
        recorder.LevelChanged += OnLevelChanged;
        recorder.ProgressChanged += OnProgressChanged;
        recorder.Stopped += OnRecordingStopped;
        _ready = true;
    }

    /// <summary>Recording stopped on its own; the argument explains why.</summary>
    public event EventHandler<string>? RecordingFailed;

    public ObservableCollection<AudioDevice> Devices { get; } = [];

    public TranscriptViewModel Transcript { get; }

    public IReadOnlyList<LanguageOption> Languages { get; } =
        AppSettings.Languages.Select(code => new LanguageOption(code, code == "ja" ? "日本語" : "Русский")).ToArray();

    public bool CanEditSettings => !IsRecording && !IsStarting;

    public bool IsIdle => !IsRecording;

    public string RecordButtonText => IsRecording ? "Остановить запись"
        : IsStarting ? "Запуск…"
        : RootFolder is null ? "Сначала выберите папку"
        : "Начать запись";

    public string LanguageText => Language.ToUpperInvariant();

    public HotkeySettings Hotkeys => _settings.Hotkeys;

    public string TrayToolTip => IsRecording
        ? $"mictap: идёт запись, {RecordedText}, язык {LanguageText}"
        : $"mictap: запись не идёт, язык {LanguageText}";

    /// <summary>Lists microphones and repairs chunks that a crash left open in the latest session.</summary>
    public void Initialize()
    {
        RefreshDevices();
        RepairLatestSession();
        if (RootFolder is null && !IsError)
        {
            ShowMessage("Выберите папку для записей.", isError: false);
        }
    }

    public Task StopRecordingAsync() => _recorder.StopAsync();

    /// <summary>Hotkey: the next language of the list, round and round.</summary>
    public void ToggleLanguage()
    {
        IReadOnlyList<string> languages = AppSettings.Languages;
        Language = languages[(languages.ToList().IndexOf(Language) + 1) % languages.Count];
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
            ShowMessage("Запись по хоткею не началась: сначала выберите папку и микрофон.", isError: true);
        }
    }

    public void ShowHotkeyProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            ShowMessage($"Хоткеи не работают:\n{string.Join("\n", problems)}\nИзмените их в {_settingsStore.FilePath} и перезапустите mictap.", isError: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Папка для записей" };
        if (RootFolder is not null && Directory.Exists(RootFolder))
        {
            dialog.InitialDirectory = RootFolder;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        RootFolder = dialog.FolderName;
        Save(_settings with { RootFolder = dialog.FolderName });
        ShowMessage("Папка для записей выбрана.", isError: false);
        RepairLatestSession();
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
        // During a recording the switch is a mark: speech from here on is recognized in the new language.
        _recorder.MarkLanguage(value);
        if (_ready)
        {
            LanguageCue.Play(value, _logger);
        }

        if (value != _settings.Language)
        {
            Save(_settings with { Language = value });
        }
    }

    partial void OnSelectedDeviceChanged(AudioDevice? value)
    {
        if (!_refreshingDevices && value is not null && value.Name != _settings.DeviceName)
        {
            Save(_settings with { DeviceName = value.Name });
        }
    }

    private bool CanStart() => !IsRecording && !IsStarting && RootFolder is not null && SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        string root = RootFolder!;
        string device = SelectedDevice!.Name;
        var options = new RecordingOptions(root, device, _settings.ChunkSeconds, _appVersion, Language);
        SessionInfo session;
        IsStarting = true;
        try
        {
            // Opening a capture device can take seconds; the window stays responsive meanwhile.
            session = await Task.Run(() => _recorder.Start(options, WaveInAudioSource.Open(device)));
        }
        catch (AudioCaptureException ex)
        {
            _logger.Error(ex, "Recording did not start");
            ShowMessage($"Запись не началась: {Describe(ex)}", isError: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Recording did not start");
            ShowMessage($"Запись не началась: нет доступа к папке {root} ({ex.Message}).", isError: true);
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
        Transcript.BeginSession(session);
        ChunkText = ChunkedWavWriter.FileName(0);
        ShowRecorded(TimeSpan.Zero);
        ShowMessage($"Идёт запись: сессия {session.Name}.", isError: false);

        if (_settings.DeviceName != device)
        {
            Save(_settings with { DeviceName = device });
        }
    }

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private Task StopAsync() => _recorder.StopAsync();

    private void RepairLatestSession()
    {
        if (RootFolder is null)
        {
            return;
        }

        RepairReport? report;
        try
        {
            report = SessionRepair.RepairLatest(RootFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Checking the latest session in {Root} failed", RootFolder);
            ShowMessage($"Не удалось проверить последнюю сессию в {RootFolder}: {ex.Message}", isError: true);
            return;
        }

        if (report is null)
        {
            return;
        }

        foreach (string chunk in report.Repaired)
        {
            _logger.Warning("Chunk left open by a crash repaired: {Chunk}", chunk);
        }

        foreach (string chunk in report.Unrecognized)
        {
            _logger.Warning("Chunk with an unrecognized header left as is: {Chunk}", chunk);
        }

        if (report.Unrecognized.Count > 0)
        {
            ShowMessage(
                $"В сессии {report.Session.Name} есть файлы с нераспознанным заголовком, они не изменены:\n{FileNames(report.Unrecognized)}.",
                isError: true);
        }
        else if (report.Repaired.Count > 0)
        {
            ShowMessage(
                $"Сессия {report.Session.Name} была прервана сбоем.\nЗаголовки восстановлены: {FileNames(report.Repaired)}.",
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
                ChunkText = ChunkedWavWriter.FileName(progress.ChunkIndex);
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
            if (stopped.Reason == StopReason.Requested)
            {
                ShowMessage($"Запись остановлена.\nСессия {stopped.Session.Name} сохранена.", isError: false);
                return;
            }

            string text = $"Запись остановлена: {Describe(stopped)}\nЗаписанное сохранено в сессии {stopped.Session.Name}.";
            ShowMessage(text, isError: true);
            RecordingFailed?.Invoke(this, text);
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

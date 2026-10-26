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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _rootFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDevice? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ChooseFolderCommand), nameof(RefreshDevicesCommand))]
    private bool _isRecording;

    [ObservableProperty]
    private double _level;

    [ObservableProperty]
    private string _sessionText = "—";

    [ObservableProperty]
    private string _chunkText = "—";

    [ObservableProperty]
    private string _recordedText = FormatMinutes(TimeSpan.Zero);

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isError;

    public MainViewModel(SettingsStore settingsStore, AppSettings settings, Recorder recorder, string appVersion, ILogger logger)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _recorder = recorder;
        _appVersion = appVersion;
        _logger = logger.ForContext<MainViewModel>();
        RootFolder = settings.RootFolder;
        recorder.LevelChanged += OnLevelChanged;
        recorder.ProgressChanged += OnProgressChanged;
        recorder.Stopped += OnRecordingStopped;
    }

    /// <summary>Recording stopped on its own; the argument explains why.</summary>
    public event EventHandler<string>? RecordingFailed;

    public ObservableCollection<AudioDevice> Devices { get; } = [];

    public bool CanEditSettings => !IsRecording;

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
        ShowMessage($"Записи будут сохраняться в {dialog.FolderName}.", isError: false);
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
            ShowMessage($"Микрофон «{_settings.DeviceName}» не найден, выбран «{SelectedDevice.Name}».", isError: true);
        }
        else
        {
            ShowMessage($"Найдено микрофонов: {devices.Count}.", isError: false);
        }
    }

    partial void OnSelectedDeviceChanged(AudioDevice? value)
    {
        if (!_refreshingDevices && value is not null && value.Name != _settings.DeviceName)
        {
            Save(_settings with { DeviceName = value.Name });
        }
    }

    private bool CanStart() => !IsRecording && RootFolder is not null && SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        string root = RootFolder!;
        string device = SelectedDevice!.Name;
        try
        {
            SessionInfo session = _recorder.Start(
                new RecordingOptions(root, device, _settings.ChunkSeconds, _appVersion), WaveInAudioSource.Open(device));
            IsRecording = true;
            SessionText = session.Name;
            ChunkText = ChunkedWavWriter.FileName(0);
            RecordedText = FormatMinutes(TimeSpan.Zero);
            ShowMessage($"Идёт запись в {session.Folder}", isError: false);
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
                $"В сессии {report.Session.Name} есть файлы с нераспознанным заголовком, они не изменены: {FileNames(report.Unrecognized)}.",
                isError: true);
        }
        else if (report.Repaired.Count > 0)
        {
            ShowMessage(
                $"Сессия {report.Session.Name} была прервана сбоем; заголовки восстановлены: {FileNames(report.Repaired)}.",
                isError: false);
        }
    }

    private void OnLevelChanged(object? sender, double peak) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (IsRecording)
            {
                Level = AudioLevel.ToMeter(peak) * 100;
            }
        });

    private void OnProgressChanged(object? sender, RecordingProgress progress) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (IsRecording)
            {
                ChunkText = ChunkedWavWriter.FileName(progress.ChunkIndex);
                RecordedText = FormatMinutes(progress.Recorded);
            }
        });

    private void OnRecordingStopped(object? sender, RecordingStopped stopped) =>
        _dispatcher.InvokeAsync(() =>
        {
            IsRecording = false;
            Level = 0;
            RecordedText = FormatMinutes(stopped.Recorded);
            if (stopped.Reason == StopReason.Requested)
            {
                ShowMessage($"Запись остановлена, сохранено в {stopped.Session.Folder}", isError: false);
                return;
            }

            string text = $"Запись остановлена: {Describe(stopped)} Записанное сохранено в {stopped.Session.Folder}";
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

    private static string Describe(RecordingStopped stopped) => stopped.Reason switch
    {
        StopReason.DeviceFailed => stopped.Error is null
            ? "микрофон прекратил захват."
            : $"ошибка микрофона ({stopped.Error.Message}).",
        StopReason.DeviceSilent => $"микрофон не передаёт звук {Recorder.SilenceTimeout.TotalSeconds:0} с — вероятно, он отключён.",
        StopReason.WriteFailed => $"ошибка записи на диск ({stopped.Error?.Message}).",
        _ => "",
    };

    private static string Describe(AudioCaptureException error) => error.Failure switch
    {
        CaptureFailure.DeviceNotFound => $"микрофон «{error.DeviceName}» не найден. Подключите его и нажмите «Обновить».",
        _ => $"микрофон «{error.DeviceName}» не запустился в формате 16 кГц / моно / 16 бит ({error.InnerException?.Message}).",
    };

    private static string FileNames(IEnumerable<string> paths) => string.Join(", ", paths.Select(Path.GetFileName));

    private static string FormatMinutes(TimeSpan recorded) => $"{recorded.TotalMinutes:0.0} мин";
}

using System.IO;
using System.Windows;
using System.Windows.Threading;
using MicTap.Core;
using MicTap.Hub;
using Serilog;

namespace MicTap.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TranscriptViewModel? _transcript;
    private GlobalHotkeys? _hotkeys;
    private HubHost? _hub;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two instances would record over each other's sessions and settings.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\mictap", out bool createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            MessageBox.Show("mictap уже запущен — проверьте значок в трее.", "mictap", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(AppPaths.LogsFolder, "mictap-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
            .CreateLogger();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error(args.Exception, "Unobserved task exception");

        string version = typeof(App).Assembly.GetName().Version!.ToString(3);
        Log.Information("mictap {Version} started", version);

        var settingsStore = new SettingsStore(AppPaths.SettingsFile);
        AppSettings settings = settingsStore.Load();
        var recorder = new Recorder(Log.Logger, TimeProvider.System);
        _transcript = new TranscriptViewModel(
            new ModelStore(AppPaths.ModelsFolder, ModelSpec.LargeV3Turbo), new ModelStore(AppPaths.ModelsFolder, ModelSpec.SileroVad),
            recorder, Log.Logger);
        var viewModel = new MainViewModel(settingsStore, settings, recorder, _transcript, version, Log.Logger);
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
        viewModel.Initialize();
        _transcript.Start();

        TranscriptionPipeline pipeline = _transcript.Pipeline;
        var agents = new AgentsState(viewModel.RootFolder, session => pipeline.SinkFor(session).Lines, TimeProvider.System, version);
        agents.Attach(recorder, pipeline);
        viewModel.UseHub(agents);
        _ = StartHubAsync(settings.HubPort, agents, viewModel);

        _hotkeys = new GlobalHotkeys();
        HotkeySettings keys = viewModel.Hotkeys;
        viewModel.ShowHotkeyProblems([
            .. new[] { _hotkeys.Register(keys.ToggleLanguage, viewModel.ToggleLanguage), _hotkeys.Register(keys.ToggleRecording, viewModel.ToggleRecording) }
                .OfType<string>(),
        ]);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            _hotkeys?.Dispose();
            _hub?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            _transcript?.Dispose();
            Log.Information("mictap exited");
            Log.CloseAndFlush();
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>The hub for agents; a taken port leaves the app working with the hub off.</summary>
    private async Task StartHubAsync(int port, AgentsState agents, MainViewModel viewModel)
    {
        try
        {
            _hub = await HubHost.StartAsync(port, agents);
            Log.Information("Agents hub listens on 127.0.0.1:{Port}", _hub.Port);
            viewModel.HubStarted(_hub.Port);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Agents hub is off: port {Port} is taken", port);
            viewModel.HubFailed($"порт {port} занят");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Not handled: the process ends, and a chunk left open is repaired on the next start.
        Log.Fatal(e.Exception, "Unhandled exception on the UI thread");
        MessageBox.Show(
            $"Непредвиденная ошибка, mictap будет закрыт:\n{e.Exception.Message}\n\nПодробности в журнале: {AppPaths.LogsFolder}",
            "mictap", MessageBoxButton.OK, MessageBoxImage.Error);
        Log.CloseAndFlush();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        Log.CloseAndFlush();
    }
}

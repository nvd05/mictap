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

        // --profile <folder>: a test copy with its own settings and logs, next to the user's mictap.
        int profileArg = Array.IndexOf(e.Args, "--profile");
        if (profileArg >= 0 && profileArg + 1 < e.Args.Length)
        {
            AppPaths.UseProfile(e.Args[ profileArg + 1 ]);
        }

        // Two instances would record over each other's sessions and settings; a profile is a separate one.
        string instance = AppPaths.Profile is null
            ? @"Local\mictap"
            : $@"Local\mictap-profile-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.Profile.ToUpperInvariant())))[ ..16 ]}";
        _singleInstance = new Mutex(initiallyOwned: true, instance, out bool createdNew);
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
        Log.Information("mictap {Version} started{Profile}", version, AppPaths.Profile is null ? "" : $" with the profile {AppPaths.Profile}");

        var settingsStore = new SettingsStore(AppPaths.SettingsFile);
        AppSettings settings = settingsStore.Load();
        var recorder = new Recorder(Log.Logger, TimeProvider.System);
        _transcript = new TranscriptViewModel(
            new ModelStore(AppPaths.ModelsFolder, ModelSpec.LargeV3Turbo), new ModelStore(AppPaths.ModelsFolder, ModelSpec.SileroVad),
            recorder, Log.Logger);
        var viewModel = new MainViewModel(settingsStore, settings, recorder, _transcript, version, Log.Logger);
        MainWindow = new MainWindow(viewModel);
        if (AppPaths.Profile is not null)
        {
            MainWindow.Title = $"mictap — тест ({Path.GetFileName(AppPaths.Profile)})";
        }

        MainWindow.Show();
        viewModel.Initialize();
        _transcript.Start();

        TranscriptionPipeline pipeline = _transcript.Pipeline;
        var agents = new AgentsState(viewModel.RootFolder, session => pipeline.SinkFor(session).Lines, TimeProvider.System, version);
        agents.Attach(recorder, pipeline);
        viewModel.UseHub(agents);
        _ = StartHubAsync(settings.HubPort, agents, viewModel);

        if (AppPaths.Profile is not null)
        {
            return; // the global hotkeys stay with the user's mictap
        }

        _hotkeys = new GlobalHotkeys();
        HotkeySettings keys = viewModel.Hotkeys;
        viewModel.ShowHotkeyProblems(
        [
            .. new[]
                {
                    _hotkeys.Register(keys.ToggleLanguage, viewModel.ToggleLanguage),
                    _hotkeys.Register(keys.ToggleRecording, viewModel.ToggleRecording),
                    _hotkeys.Register(keys.ForcePush, viewModel.SendToAgentsNow),
                }
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

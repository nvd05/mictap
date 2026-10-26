using System.IO;
using System.Windows;
using System.Windows.Threading;
using MicTap.Core;
using Serilog;

namespace MicTap.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

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
        var viewModel = new MainViewModel(
            settingsStore, settingsStore.Load(), new Recorder(Log.Logger, TimeProvider.System), version, Log.Logger);
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
        viewModel.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            Log.Information("mictap exited");
            Log.CloseAndFlush();
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }

        base.OnExit(e);
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

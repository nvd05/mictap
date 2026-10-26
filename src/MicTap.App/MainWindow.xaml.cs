using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using H.NotifyIcon.Core;
using MaterialDesignThemes.Wpf;

namespace MicTap.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _confirmingClose;
    private bool _closeConfirmed;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.RecordingFailed += OnRecordingFailed;
        UpdateTrayIcon();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            // Minimize goes to the tray. Window.Hide, not H.NotifyIcon's Hide extension:
            // that one turns on Efficiency Mode, which throttles the process and can starve capture.
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        TrayIcon.Dispose();
        base.OnClosed(e);
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.IsRecording)
        {
            return;
        }

        e.Cancel = true;
        if (_confirmingClose)
        {
            return;
        }

        _confirmingClose = true;

        // Window.Show is not allowed while Closing runs: go on once the cancelled close has unwound.
        await System.Windows.Threading.Dispatcher.Yield();
        ShowFromTray();
        if (await DialogHost.Show(FindResource("CloseDialog"), "MainDialog") is not true)
        {
            _confirmingClose = false;
            return;
        }

        await _viewModel.StopRecordingAsync();
        _closeConfirmed = true;
        Close();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void UpdateTrayIcon() =>
        TrayIcon.IconSource = (ImageSource)FindResource(_viewModel.IsRecording ? "RecordingIcon" : "IdleIcon");

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRecording))
        {
            UpdateTrayIcon();
        }
    }

    private void OnRecordingFailed(object? sender, string message)
    {
        if (!IsVisible)
        {
            TrayIcon.ShowNotification("mictap: запись остановлена", message, NotificationIcon.Warning);
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTrayOpen(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        Close();
    }
}

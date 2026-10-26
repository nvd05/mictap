using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using H.NotifyIcon.Core;

namespace MicTap.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

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

    private void OnTrayOpen(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        Close();
    }
}

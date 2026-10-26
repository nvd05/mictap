using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon.Core;
using MicTap.Core;
using MaterialDesignThemes.Wpf;

namespace MicTap.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _confirmingClose;
    private bool _closeConfirmed;
    private System.Drawing.Icon? _trayIcon;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.RecordingFailed += OnRecordingFailed;
        viewModel.NewProjectRequested += (_, _) => _ = ShowProjectDialogAsync(ProjectEditor.ForNew(), viewModel.CreateProject);
        viewModel.EditProjectRequested += (_, project) =>
            _ = ShowProjectDialogAsync(ProjectEditor.For(project), settings => viewModel.SaveProject(project, settings));
        viewModel.RenameSessionRequested += (_, session) => _ = ShowRenameDialogAsync(session);
        viewModel.SettingsRequested += (_, _) => _ = ShowSettingsAsync();
        viewModel.Transcript.Items.CollectionChanged += (_, _) => FollowTranscript();
        viewModel.Transcript.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TranscriptViewModel.NowText) or nameof(TranscriptViewModel.IsLive))
            {
                FollowTranscript();
            }
        };
        UpdateTrayIcon();
        AddFailureSimulation();
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

        // A maximized chromeless window reaches past the screen by its resize border: keep the content inside.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaximizeIcon.Kind = WindowState == WindowState.Maximized ? PackIconKind.WindowRestore : PackIconKind.WindowMaximize;
    }

    protected override void OnClosed(EventArgs e)
    {
        TrayIcon.Dispose();
        _trayIcon?.Dispose();
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

    /// <summary>The project dialog; it stays open while what was typed is not valid, with the reason shown in it.</summary>
    private async Task ShowProjectDialogAsync(ProjectEditor editor, Action<ProjectSettings> apply)
    {
        var content = (FrameworkElement)FindResource("ProjectDialog");
        content.DataContext = editor;
        ProjectSettings? settings = null;
        await DialogHost.Show(content, "MainDialog", (object _, DialogClosingEventArgs e) =>
        {
            if (e.Parameter is true && (settings = editor.Build()) is null)
            {
                e.Cancel();
            }
        });

        if (settings is not null)
        {
            apply(settings);
        }
    }

    private async Task ShowRenameDialogAsync(SessionNode session)
    {
        var content = (FrameworkElement)FindResource("RenameDialog");
        var editor = new RenameEditor(session.Name);
        content.DataContext = editor;
        if (await DialogHost.Show(content, "MainDialog") is true)
        {
            await _viewModel.RenameSessionAsync(session, editor.Name);
        }
    }

    private async Task ShowSettingsAsync()
    {
        var content = (FrameworkElement)FindResource("SettingsDialog");
        content.DataContext = _viewModel;
        await DialogHost.Show(content, "MainDialog");
    }

    /// <summary>New lines scroll the transcript to its end unless the user scrolled up to read.</summary>
    private void FollowTranscript()
    {
        bool atEnd = TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 48;
        if (atEnd)
        {
            Dispatcher.InvokeAsync(TranscriptScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
        }
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

    private void UpdateTrayIcon()
    {
        System.Drawing.Icon? previous = _trayIcon;
        _trayIcon = TrayIcons.Render(_viewModel.IsRecording, _viewModel.Language);
        TrayIcon.Icon = _trayIcon;
        previous?.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRecording) or nameof(MainViewModel.Language))
        {
            UpdateTrayIcon();
        }
    }

    /// <summary>Debug builds only: a tray item that makes the next recognition pass fail, to check recovery by hand.</summary>
    [Conditional("DEBUG")]
    private void AddFailureSimulation()
    {
        var item = new MenuItem { Header = "Имитировать сбой распознавания", Icon = new PackIcon { Kind = PackIconKind.AlertOctagonOutline } };
        item.Click += (_, _) => _viewModel.Transcript.SimulateFailure();
        TrayIcon.ContextMenu.Items.Insert(1, item);
    }

    private void OnRecordingFailed(object? sender, string message)
    {
        if (!IsVisible)
        {
            TrayIcon.ShowNotification("mictap: запись остановлена", message, NotificationIcon.Warning);
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => _viewModel.Select(e.NewValue);

    private void OnDismissMessage(object sender, RoutedEventArgs e) => _viewModel.DismissMessage();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTrayOpen(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        Close();
    }
}

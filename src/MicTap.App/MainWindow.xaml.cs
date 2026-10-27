using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private Point _dragStart;

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
        viewModel.DeleteRequested += (_, request) => _ = ConfirmDeleteAsync(request);
        viewModel.SettingsRequested += (_, _) => _ = ShowSettingsAsync();
        viewModel.Transcript.Items.CollectionChanged += (_, _) => FollowTranscript();
        viewModel.Transcript.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TranscriptViewModel.NowText))
            {
                // A long draft keeps its latest words in view.
                Dispatcher.InvokeAsync(DraftScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
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

    private async Task ShowRenameDialogAsync(SessionNode? session)
    {
        var content = (FrameworkElement)FindResource("RenameDialog");
        var editor = session is null
            ? new RenameEditor("Имя новой сессии", _viewModel.NameToRename(null), "Пустое — время начала записи.", "Сохранить")
            : session.IsRecording
                ? new RenameEditor("Переименовать сессию", _viewModel.NameToRename(session), "Папка получит имя после остановки записи.")
                : new RenameEditor("Переименовать сессию", session.Name, "Папка сессии переименуется вместе с текстом и звуком.");
        content.DataContext = editor;
        if (await DialogHost.Show(content, "MainDialog") is true)
        {
            await _viewModel.ApplyRenameAsync(session, editor.Name);
        }
    }

    /// <summary>A delete goes ahead only after a yes in the dialog.</summary>
    private async Task ConfirmDeleteAsync(DeleteRequest request)
    {
        var content = (FrameworkElement)FindResource("DeleteDialog");
        content.DataContext = request;
        if (await DialogHost.Show(content, "MainDialog") is true)
        {
            await request.Delete();
        }
    }

    /// <summary>The session's menu, also from its name above the text; «Перенести в проект» lists the other projects.</summary>
    private void OnSessionMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        SessionNode? session = (menu.PlacementTarget as FrameworkElement)?.DataContext as SessionNode;
        MenuItem move = menu.Items.OfType<MenuItem>().Single(item => "Move".Equals(item.Tag));
        move.Items.Clear();
        foreach (ProjectNode project in _viewModel.Projects.Where(p => p != session?.Project))
        {
            var item = new MenuItem { Header = project.Name, Icon = new PackIcon { Kind = PackIconKind.FolderOutline } };
            item.Click += async (_, _) => await _viewModel.MoveAsync(session!, project);
            move.Items.Add(item);
        }

        move.IsEnabled = session is not null && !session.IsRecording && move.Items.Count > 0;
    }

    private void OnSessionMenuClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void OnSessionMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    /// <summary>Dragging a session onto another project moves it there.</summary>
    private void OnSessionMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: SessionNode session } row)
        {
            return;
        }

        Vector moved = e.GetPosition(this) - _dragStart;
        if (Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            DragDrop.DoDragDrop(row, new DataObject(typeof(SessionNode), session), DragDropEffects.Move);
        }
    }

    private void OnProjectDragOver(object sender, DragEventArgs e)
    {
        var row = (Border)sender;
        bool accepts = row.DataContext is ProjectNode target && e.Data.GetData(typeof(SessionNode)) is SessionNode session
            && session.Project != target && !session.IsRecording;
        e.Effects = accepts ? DragDropEffects.Move : DragDropEffects.None;
        if (accepts)
        {
            row.Background = (Brush)FindResource("InfoContainerBrush");
        }

        e.Handled = true;
    }

    private void OnProjectDragLeave(object sender, DragEventArgs e) => ((Border)sender).ClearValue(Border.BackgroundProperty);

    private async void OnProjectDrop(object sender, DragEventArgs e)
    {
        var row = (Border)sender;
        row.ClearValue(Border.BackgroundProperty);
        if (row.DataContext is ProjectNode target && e.Data.GetData(typeof(SessionNode)) is SessionNode session)
        {
            await _viewModel.MoveAsync(session, target);
        }
    }

    private async Task ShowSettingsAsync()
    {
        var content = (FrameworkElement)FindResource("SettingsDialog");
        content.DataContext = _viewModel;
        _viewModel.ShowRouterAiKey();
        await DialogHost.Show(content, "MainDialog");
    }

    /// <summary>The key goes from the password box straight to the Credential Manager; the box is cleared at once.</summary>
    private void OnSaveRouterAiKey(object sender, RoutedEventArgs e)
    {
        PasswordBox box = ((Panel)((FrameworkElement)sender).Parent).Children.OfType<PasswordBox>().Single();
        _viewModel.SaveRouterAiKey(box.Password);
        box.Clear();
    }

    private void OnDeleteRouterAiKey(object sender, RoutedEventArgs e) => _viewModel.DeleteRouterAiKey();

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

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MicTap.App;
using MicTap.Core;
using Serilog;

namespace MicTap.App.Tests;

public sealed class TranscriptScrollTests
{
    [UiFact]
    public async Task Appending_keeps_reading_position_and_follows_only_at_the_end()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "mictap-ui-tests", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                AppPaths.UseProfile(root);
                var app = new global::MicTap.App.App();
                app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                using var logger = new LoggerConfiguration().CreateLogger();
                var recorder = new Recorder(logger, TimeProvider.System);
                using var transcript = new TranscriptViewModel(
                    new ModelStore(AppPaths.ModelsFolder, ModelSpec.LargeV3Turbo),
                    new ModelStore(AppPaths.ModelsFolder, ModelSpec.SileroVad), recorder, logger);
                var settings = new AppSettings { RootFolder = root };
                var vm = new MainViewModel(new SettingsStore(Path.Combine(root, "settings.json")), settings, recorder, transcript, "test", logger);
                window = new MainWindow(vm) { Width = 1050, Height = 750 };
                window.Show();
                var scroll = (ScrollViewer)window.FindName("TranscriptScroll");
                void Pump() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                void Append(int seq) => transcript.Items.Add(new TranscriptItem(
                    new TranscriptLine(seq, seq, seq + 1, "ru", string.Join(" ", Enumerable.Repeat($"Фраза {seq}.", 80)), false),
                    DateTimeOffset.Now, false, null, false, _ => { }));

                for (int i = 1; i <= 12; i++) Append(i);
                Pump();
                Assert.True(scroll.ScrollableHeight > 500);
                Assert.InRange(scroll.ScrollableHeight - scroll.VerticalOffset, 0, 2);
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 3);
                Pump();
                double before = scroll.VerticalOffset;
                for (int i = 13; i <= 17; i++)
                {
                    Append(i);
                    Pump();
                    Assert.InRange(Math.Abs(scroll.VerticalOffset - before), 0, 1);
                }

                // Reading just above the bottom must not be mistaken for following new speech.
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight - 24);
                Pump();
                before = scroll.VerticalOffset;
                Append(18);
                Pump();
                Assert.InRange(Math.Abs(scroll.VerticalOffset - before), 0, 1);

                scroll.ScrollToEnd();
                Pump();
                Append(19);
                Pump();
                Assert.InRange(scroll.ScrollableHeight - scroll.VerticalOffset, 0, 2);

                vm.CreateProject(new ProjectSettings("Старый проект", [ "ru" ], AudioRetention.Keep));
                ProjectInfo project = vm.SelectedProject!;
                Task rename = vm.SaveProjectAsync(project, project.Settings with { Name = "Новый проект" });
                while (!rename.IsCompleted)
                {
                    Pump();
                    Thread.Sleep(10);
                }
                rename.GetAwaiter().GetResult();
                Assert.False(Directory.Exists(project.Folder));
                Assert.Equal(Path.Combine(root, "Новый проект"), vm.SelectedProject!.Folder);
                Assert.Equal("Новый проект", Projects.Load(vm.SelectedProject.Folder).Name);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                window?.Close();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (!new ModelStore(AppPaths.ModelsFolder, ModelSpec.SileroVad).IsReady)
        {
            Skip = "The WPF integration test requires the app's existing VAD model; it never downloads models.";
        }
    }
}

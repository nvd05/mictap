using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                AppPaths.UseProfile(Path.Combine(root, "макет"));
                var app = new global::MicTap.App.App();
                app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                using var logger = new LoggerConfiguration().CreateLogger();
                var recorder = new Recorder(logger, TimeProvider.System);
                using var transcript = new TranscriptViewModel(
                    new ModelStore(AppPaths.ModelsFolder, ModelSpec.LargeV3Turbo),
                    new ModelStore(AppPaths.ModelsFolder, ModelSpec.SileroVad), recorder, logger);
                var settings = new AppSettings { RootFolder = root };
                var vm = new MainViewModel(new SettingsStore(Path.Combine(root, "settings.json")), settings, recorder, transcript,
                    typeof(MainWindow).Assembly.GetName().Version!.ToString(3), logger);
                window = new MainWindow(vm) { Width = 1050, Height = 750 };
                window.Show();
                var scroll = (ScrollViewer)window.FindName("TranscriptScroll");
                void Pump() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                void Export(string name)
                {
                    if (Environment.GetEnvironmentVariable("MICTAP_UI_PREVIEW_FOLDER") is not { Length: > 0 } output) return;
                    Directory.CreateDirectory(output);
                    Pump();
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, name + ".png"));
                    encoder.Save(file);
                }

                vm.PageIndex = (int)AppPage.Settings;
                vm.SettingsSection = 2;
                window.Height = 860;
                Export("mictap-settings-new-projects");
                window.Height = 750;
                vm.PageIndex = (int)AppPage.Records;
                Pump();
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

                string existingProject = File.ReadAllText(vm.SelectedProject.SettingsFile);
                ProjectEditor defaults = vm.DefaultProjectEditor;
                defaults.Languages.Choices.Single(c => c.Language.Code == "ja").IsSelected = true;
                defaults.PhrasePauseSeconds = 4;
                defaults.LiveDraftSeconds = 8;
                vm.SaveProjectDefaultsCommand.Execute(null);
                var restarted = new SettingsStore(Path.Combine(root, "settings.json")).Load();
                Assert.Equal([ "ru", "ja" ], restarted.NewProjectDefaults.Languages);
                Assert.Equal(8, restarted.NewProjectDefaults.LiveDraftSeconds);
                Assert.Equal(existingProject, File.ReadAllText(vm.SelectedProject.SettingsFile));
                ProjectEditor next = vm.NewProjectEditor();
                Assert.Equal([ "ru", "ja" ], next.Languages.SelectedCodes);
                Assert.Equal(4, next.PhrasePauseSeconds);
                Assert.Equal(8, next.LiveDraftSeconds);

                SessionInfo session = SessionFolders.Open(vm.SelectedProject.Folder, "Ручной ввод");
                ProjectNode projectNode = vm.Projects.Single(p => p.Project.Folder == vm.SelectedProject.Folder);
                var sessionNode = new SessionNode(session, projectNode);
                projectNode.Sessions.Add(sessionNode);
                vm.Select(sessionNode);
                vm.DismissMessage();
                using var source = new MicTap.Core.Tests.FakeAudioSource();
                recorder.Start(new RecordingOptions(session, "Synthetic", 60, "test", "ru", AudioRetention.None), source);
                vm.IsRecording = true;
                transcript.BeginSession(session, null);
                foreach (int draft in new[] { 3, 0 })
                {
                    transcript.SetDraftInterval(draft);
                    transcript.NowText = draft > 0 ? "Незаконченная голосовая мысль" : "";
                    string originalDraft = transcript.NowText;
                    source.Push(new byte[ 3200 ]);
                    vm.ManualText = $"Ручной текст · черновик {draft}";
                    if (draft == 3) Export("mictap-unified-composer");
                    Assert.True(vm.SendToAgentsCommand.CanExecute(null));
                    vm.SendToAgentsCommand.Execute(null);
                    Pump();
                    Assert.Equal(originalDraft, transcript.NowText);
                    Assert.Equal(draft == 0, transcript.IsDraftOff);
                    Assert.Equal("", vm.ManualText);
                    Assert.Contains(transcript.Items, item => item.Text == $"Ручной текст · черновик {draft}");
                }
                Task stop = vm.StopRecordingAsync();
                while (!stop.IsCompleted) { Pump(); Thread.Sleep(10); }
                stop.GetAwaiter().GetResult();
                Assert.Equal(2, new JsonlTranscriptSink(session.Folder).Lines.Count);
                var surface = (FrameworkElement)window.FindName("ComposerSurface");
                var grip = (System.Windows.Controls.Primitives.Thumb)window.FindName("ComposerResize");
                double oldHeight = surface.Height;
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, -50)
                {
                    RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent,
                });
                Pump();
                Assert.Equal(oldHeight + 50, surface.Height);
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, -50, false)
                {
                    RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                });
                Assert.Equal(surface.Height, new SettingsStore(Path.Combine(root, "settings.json")).Load().ComposerHeight);
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

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;
using Serilog;

namespace MicTap.App;

/// <summary>A segment's draft text; an edit is saved to transcript.jsonl when the text box loses focus.</summary>
public sealed class TranscriptItem(TranscriptLine line, Action<TranscriptItem> edited) : ObservableObject
{
    private string _text = line.Text;

    public TranscriptLine Line { get; private set; } = line;

    public string TimeText => TimeSpan.FromSeconds(Line.Start).ToString(@"hh\:mm\:ss");

    public string LanguageText => Line.Lang.ToUpperInvariant();

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                edited(this);
            }
        }
    }

    internal void Saved(TranscriptLine line) => Line = line;
}

/// <summary>
/// Models (downloaded on first start), the recognition runtime and its recovery, live text and the editable text of
/// recognized segments.
/// </summary>
public sealed partial class TranscriptViewModel : ObservableObject, IDisposable
{
    private readonly ModelStore _whisperModel;
    private readonly ModelStore _vadModel;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HttpClient _http = new();
    private readonly HttpClient _cloudHttp = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly CancellationTokenSource _stop = new();
    private readonly TranscriberHost _host;
    private readonly TranscriptionPipeline _pipeline;
    private readonly Task<Func<SpeechDetector>> _detectors;
    private VoiceActivityModel? _vad;
    private SessionInfo? _session;
    private SessionInfo? _liveSession;
    private bool _live;
    private DateTime _endedAt;
    private string? _cloudModel;
    private bool _cloudFailing;
    private CloudUsage _cloudSpent = CloudUsage.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecognitionText))]
    private string _runtimeText = "—";

    [ObservableProperty]
    private bool _runtimeWarning;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>The draft is off in the settings: the recording bar says so instead of «Слушаю…».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecognitionText))]
    private bool _isDraftOff;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadPercent;

    [ObservableProperty]
    private string _nowText = "";

    [ObservableProperty]
    private bool _isSilence;

    [ObservableProperty]
    private bool _isLiveBusy;

    [ObservableProperty]
    private string _queueText = "";

    [ObservableProperty]
    private bool _isRetranscribing;

    /// <summary>A recording runs: its draft, the speech not yet in the text, is shown in the recording bar.</summary>
    [ObservableProperty]
    private bool _isLive;

    private int _pending;

    public TranscriptViewModel(ModelStore whisperModel, ModelStore vadModel, Recorder recorder, ILogger logger)
    {
        _whisperModel = whisperModel;
        _vadModel = vadModel;
        _logger = logger.ForContext<TranscriptViewModel>();
        _host = new TranscriberHost(LoadTranscriberAsync, logger, TimeProvider.System);
        _host.StateChanged += (_, state) => _dispatcher.InvokeAsync(() => ShowState(state));
        _detectors = Task.Run(LoadDetectorsAsync);
        _pipeline = new TranscriptionPipeline(
            _host, _detectors, logger,
            (session, model) => new RouterAiTranscriber(_cloudHttp, model, () => CloudKeys.Read(), spend => OnCloudSpent(session, spend)),
            CurrentModel);
        _pipeline.LiveTextChanged += OnLiveText;
        _pipeline.LiveBusyChanged += (_, busy) => _dispatcher.InvokeAsync(() => IsLiveBusy = busy && _live);
        _pipeline.SegmentTranscribed += OnSegmentTranscribed;
        _pipeline.QueueChanged += (_, count) =>
        {
            Volatile.Write(ref _pending, count);
            _dispatcher.InvokeAsync(() => QueueText = count > 0 ? $"ждут распознавания: {count}" : "");
        };
        _pipeline.Failed += OnFailed;
        _pipeline.CloudProblem += (_, problem) => _dispatcher.InvokeAsync(() => ShowCloudProblem(problem));
        _pipeline.RecordingTranscribed += (_, session) => _dispatcher.InvokeAsync(() =>
        {
            if (session == _liveSession && !_live)
            {
                ShowCloud(null, session); // its last phrases are in: nothing goes to the cloud any more
            }
        });
        _pipeline.Attach(recorder);
    }

    public ObservableCollection<TranscriptItem> Items { get; } = [];

    /// <summary>
    /// The status line's «Распознавание»: who recognizes the phrases and the draft. The graphics card's runtime alone
    /// when it does both; with Router AI, the phrases' model and what the session spent, and the draft apart.
    /// </summary>
    public string RecognitionText
    {
        get
        {
            string draft = IsDraftOff ? "черновик выключен" : $"черновик — {RuntimeText}";
            if (_cloudModel is null)
            {
                return IsDraftOff ? $"{RuntimeText}, черновик выключен" : RuntimeText;
            }

            string phrases = _cloudFailing
                ? "фразы — Router AI: сбой"
                : $"фразы — Router AI · {_cloudModel} · {_cloudSpent.Rubles:0.00} ₽";
            return $"{phrases}, {draft}";
        }
    }

    /// <summary>The session whose text <see cref="Items"/> holds.</summary>
    public SessionInfo? ShownSession => _session;

    /// <summary>Recognized segments and their sinks, for the agents hub.</summary>
    public TranscriptionPipeline Pipeline => _pipeline;

    /// <summary>Loads (and on first start downloads) the models. Recording works meanwhile; its speech waits in the queue.</summary>
    public void Start()
    {
        _host.Start();
        _pipeline.Start();
    }

    /// <summary>
    /// A project's engine was switched: the recording's status says where its phrases go, and a phrase waiting for its
    /// next cloud try goes again at once, with the new engine.
    /// </summary>
    public void EngineSwitched(SessionInfo? recording, string? model)
    {
        if (recording is not null && _live && recording == _liveSession)
        {
            ShowCloud(model, recording);
        }

        _pipeline.EngineChanged();
    }

    /// <summary>How often the draft is recognized, in seconds; 0 turns it off. Applies to the recording under way too.</summary>
    public void SetDraftInterval(int seconds)
    {
        _pipeline.LiveInterval = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        IsDraftOff = seconds == 0;
        if (IsDraftOff)
        {
            NowText = "";
            IsSilence = false;
            IsLiveBusy = false;
        }
    }

    /// <summary>Shows the text of <paramref name="session"/> in «Записано»; null clears it.</summary>
    public void ShowSession(SessionInfo? session)
    {
        _session = session;
        Items.Clear();
        if (session is null || !File.Exists(Path.Combine(session.Folder, JsonlTranscriptSink.FileName)))
        {
            return;
        }

        try
        {
            foreach (TranscriptLine line in _pipeline.SinkFor(session).Lines)
            {
                Items.Add(new TranscriptItem(line, item => SaveEdit(session, item)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _logger.Error(ex, "Reading the transcript of {Session} failed", session.Name);
            StatusText = $"Текст сессии {session.Name} не прочитан: {ex.Message}";
        }
    }

    /// <summary>
    /// A recording into <paramref name="session"/> began: its earlier text stays, new lines follow, live text runs.
    /// <paramref name="cloudModel"/> names the Router AI model its phrases go to, if any.
    /// </summary>
    public void BeginSession(SessionInfo session, string? cloudModel)
    {
        ShowCloud(cloudModel, session);
        ShowSession(session);
        _liveSession = session;
        _live = true;
        IsLive = true;
        NowText = "";
        IsSilence = false;
    }

    public void EndSession()
    {
        _endedAt = DateTime.UtcNow;
        _live = false;
        IsLive = false;
        IsLiveBusy = false;
        NowText = "";
        IsSilence = false;
    }

    /// <summary>
    /// Recognizes <paramref name="session"/> again from its audio, cut into phrases by the project's
    /// <paramref name="phrasePause"/>; the old transcript is kept as a .bak file. Sessions recorded before v0.3 are
    /// taken as spoken in <paramref name="language"/>.
    /// </summary>
    public async Task RetranscribeAsync(SessionInfo session, string language, TimeSpan phrasePause)
    {
        IsRetranscribing = true;
        _session = session;
        Items.Clear();
        QueueText = "распознаю заново…";
        try
        {
            var retranscriber = new SessionRetranscriber(_host, await _detectors, TimeProvider.System, _logger, phrasePause);
            var progress = new Progress<double>(done => QueueText = $"распознаю заново: {done:P0}");
            RetranscribeResult result = await Task.Run(() => retranscriber.RunAsync(session, language, progress, _stop.Token));
            _pipeline.ForgetSink(session);
            foreach (TranscriptLine line in result.Lines)
            {
                Items.Add(new TranscriptItem(line, item => SaveEdit(session, item)));
            }

            StatusText = result.Backup is null
                ? $"Сессия {session.Name} распознана заново: фраз {result.Lines.Count}."
                : $"Сессия {session.Name} распознана заново: фраз {result.Lines.Count}.\nПрежний текст — {Path.GetFileName(result.Backup)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _logger.Error(ex, "Recognizing {Session} again failed", session.Name);
            StatusText = $"Распознать заново не удалось: {ex.Message}";
        }
        finally
        {
            QueueText = "";
            IsRetranscribing = false;
        }
    }

    /// <summary>
    /// Recognizes the session's audio with every picked engine on the same phrases, into its compare folder, and opens
    /// comparison.md. The paid answers count in the session's cloud.json like any other.
    /// </summary>
    public async Task CompareAsync(SessionInfo session, string language, TimeSpan phrasePause, IReadOnlyList<string?> models)
    {
        IsRetranscribing = true;
        QueueText = "сравниваю модели…";
        var spent = new Dictionary<string, double>();
        try
        {
            ComparedEngine[] engines =
            [
                .. models.Select(model => model is null
                    ? new ComparedEngine("Видеокарта · Whisper", new HostTranscriber(_host))
                    : new ComparedEngine(
                        model,
                        new RouterAiTranscriber(_cloudHttp, model, () => CloudKeys.Read(), spend =>
                        {
                            lock (spent)
                            {
                                spent[ model ] = spent.GetValueOrDefault(model) + (spend.Rubles ?? 0);
                            }

                            OnCloudSpent(session, spend);
                        }),
                        () =>
                        {
                            lock (spent)
                            {
                                return spent.GetValueOrDefault(model);
                            }
                        })),
            ];
            var comparison = new SessionComparison(await _detectors, TimeProvider.System, _logger, phrasePause);
            var progress = new Progress<double>(done => QueueText = $"сравниваю модели: {done:P0}");
            ComparisonResult result = await Task.Run(() => comparison.RunAsync(session, language, engines, progress, _stop.Token));
            double rubles = result.Engines.Sum(e => e.Rubles);
            StatusText = $"Сравнение готово: моделей {result.Engines.Count}, {rubles:0.00} ₽.\n{Path.Combine(result.Folder, "comparison.md")}";
            Process.Start(new ProcessStartInfo(Path.Combine(result.Folder, "comparison.md")) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _logger.Error(ex, "Comparing engines on {Session} failed", session.Name);
            StatusText = $"Сравнить не удалось: {ex.Message}";
        }
        finally
        {
            QueueText = "";
            IsRetranscribing = false;
        }
    }

    /// <summary>
    /// Completes once no speech segment waits for recognition (or after <paramref name="limit"/>): a session folder may be
    /// renamed only after its last lines are written.
    /// </summary>
    public async Task<bool> WhenIdleAsync(TimeSpan limit)
    {
        // The last speech of a stopped recording reaches the queue a moment after the stop.
        TimeSpan settle = TimeSpan.FromSeconds(1.5) - (DateTime.UtcNow - _endedAt);
        if (settle > TimeSpan.Zero)
        {
            await Task.Delay(settle);
        }

        DateTime until = DateTime.UtcNow + limit;
        while (Volatile.Read(ref _pending) > 0)
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }

            await Task.Delay(200);
        }

        await Task.Delay(300); // the count drops just before the line is appended
        return true;
    }

    /// <summary>The session folder was renamed: its transcript is read from the new place.</summary>
    public void SessionRenamed(SessionInfo before, SessionInfo after)
    {
        _pipeline.ForgetSink(before);
        if (_session == before)
        {
            ShowSession(after);
        }
    }

    /// <summary>The session folder went to the Recycle Bin: its transcript is no longer read or shown.</summary>
    public void SessionDeleted(SessionInfo session)
    {
        _pipeline.ForgetSink(session);
        if (_session == session)
        {
            ShowSession(null);
        }
    }

    /// <summary>Makes the next recognition pass fail, to see the transcriber come back on its own.</summary>
    public void SimulateFailure()
    {
        _logger.Warning("Transcriber failure simulated by the user");
        _host.InjectFailure();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _pipeline.Dispose();
        _host.Dispose();
        _vad?.Dispose();
        _http.Dispose();
        _cloudHttp.Dispose();
    }

    private async Task<Func<SpeechDetector>> LoadDetectorsAsync()
    {
        TimeSpan retry = TranscriberHost.FirstRetry;
        while (!_vadModel.IsReady)
        {
            try
            {
                _logger.Information("Downloading {Model} from {Source}", _vadModel.Spec.FileName, _vadModel.Spec.Source);
                await _vadModel.DownloadAsync(_http, new Progress<double>(), _stop.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                _logger.Error(ex, "Downloading the VAD model failed; next attempt in {Retry}", retry);
                await _dispatcher.InvokeAsync(() => StatusText = $"Не удалось скачать модель VAD: {ex.Message}\nПовтор через {retry.TotalSeconds:0} с.");
                await Task.Delay(retry, _stop.Token);
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, TranscriberHost.MaxRetry.Ticks));
            }
        }

        _vad = VoiceActivityModel.Load(_vadModel.ModelPath);
        _logger.Information("VAD model {Model} loaded", _vadModel.Spec.FileName);
        return _vad.CreateDetector;
    }

    private async Task<ITranscriber> LoadTranscriberAsync(CancellationToken cancellation)
    {
        if (!_whisperModel.IsReady)
        {
            await DownloadWhisperAsync(cancellation);
        }

        return await Task.Run(() => WhisperTranscriber.Load(_whisperModel.ModelPath, _logger), cancellation);
    }

    private async Task DownloadWhisperAsync(CancellationToken cancellation)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            IsDownloading = true;
            StatusText = $"Скачивается модель распознавания ({_whisperModel.Spec.Size / 1e9:0.0} ГБ) в "
                + $"{Path.GetDirectoryName(_whisperModel.ModelPath)}.";
        });
        _logger.Information("Downloading {Model} from {Source}", _whisperModel.Spec.FileName, _whisperModel.Spec.Source);
        try
        {
            await _whisperModel.DownloadAsync(_http, new Progress<double>(done =>
            {
                DownloadPercent = done * 100;
                RuntimeText = $"скачивание {done:P0}";
            }), cancellation);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => IsDownloading = false);
        }

        _logger.Information("Model downloaded to {Path}", _whisperModel.ModelPath);
    }

    private void ShowState(TranscriberState state)
    {
        switch (state.Status)
        {
            case TranscriberStatus.Loading when !IsDownloading:
                RuntimeText = "загрузка…";
                RuntimeWarning = false;
                StatusText = "Загружается модель распознавания.";
                break;
            case TranscriberStatus.Ready:
                RuntimeText = state.Runtime!;
                RuntimeWarning = !state.OnGpu;
                StatusText = state.OnGpu
                    ? ""
                    : "CUDA не загрузилась — распознавание идёт на CPU, живой текст запаздывает.\n"
                      + "Нужны драйвер NVIDIA 580+ и CUDA Toolkit 13; после установки перезапустите mictap.";
                if (!state.OnGpu)
                {
                    _logger.Warning("Recognition runs on the CPU: the CUDA runtime did not load");
                }

                break;
            case TranscriberStatus.Failed:
                RuntimeText = "сбой";
                RuntimeWarning = true;
                StatusText = $"Распознавание упало: {state.Error?.Message}\n"
                    + $"Перезапуск через {state.RetryIn.TotalSeconds:0} с. Запись идёт, фразы ждут в очереди.";
                break;
        }
    }

    /// <summary>Pipeline thread: the engine a session's project uses now; its project.json says, or the recording's own on a bad read.</summary>
    private string? CurrentModel(SessionInfo session, string? recordingModel)
    {
        try
        {
            return Projects.Load(Path.GetDirectoryName(session.Folder)!).Settings.CloudModelInUse;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Warning(ex, "Project of {Session} not read; its phrases keep {Model}", session.Name, recordingModel ?? "this computer");
            return recordingModel;
        }
    }

    private void ShowCloud(string? model, SessionInfo session)
    {
        _cloudModel = model;
        _cloudFailing = false;
        try
        {
            _cloudSpent = model is null ? CloudUsage.None : CloudUsageLog.Read(session);
        }
        catch (InvalidDataException ex)
        {
            _logger.Error(ex, "Cloud usage of {Session} not read", session.Name);
            _cloudSpent = CloudUsage.None;
        }

        ShowCloudText();
    }

    private void ShowCloudText() => OnPropertyChanged(nameof(RecognitionText));

    /// <summary>Pipeline thread: one cloud answer is paid for; the session's cloud.json adds it up.</summary>
    private void OnCloudSpent(SessionInfo session, CloudSpend spend)
    {
        CloudUsage total;
        try
        {
            total = CloudUsageLog.Add(session, spend);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error(ex, "Cloud usage of {Session} not saved", session.Name);
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            if (session == _liveSession && _cloudModel is not null)
            {
                _cloudSpent = total;
                ShowCloudText();
            }
        });
    }

    private void ShowCloudProblem(string? problem)
    {
        if (_cloudModel is null)
        {
            return;
        }

        if (problem is not null)
        {
            _cloudFailing = true;
            ShowCloudText();
            StatusText = $"Router AI: {problem}. Фразы ждут в очереди, запись идёт.";
        }
        else if (_cloudFailing)
        {
            _cloudFailing = false;
            ShowCloudText();
            StatusText = "";
        }
    }

    private void OnLiveText(object? sender, LiveTranscript live) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (_live && live.Session == _liveSession && !IsDraftOff)
            {
                NowText = live.Text;
                IsSilence = live.Silence;
            }
        });

    private void OnSegmentTranscribed(object? sender, SegmentTranscript segment) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (segment.Session == _session)
            {
                Items.Add(new TranscriptItem(segment.Line, item => SaveEdit(segment.Session, item)));
            }

            if (_live && segment.Session == _liveSession)
            {
                // The draft held this phrase; the next live pass, right after, brings what follows it.
                NowText = "";
                IsSilence = false;
            }
        });

    private void OnFailed(object? sender, Exception error) =>
        _dispatcher.InvokeAsync(() =>
        {
            IsLiveBusy = false;
            RuntimeText = "ошибка";
            RuntimeWarning = true;
            StatusText = $"Распознавание остановлено: {error.Message}\nЗапись продолжается.";
        });

    private void SaveEdit(SessionInfo session, TranscriptItem item)
    {
        try
        {
            item.Saved(_pipeline.SinkFor(session).Edit(item.Line.Seq, item.Text));
            _logger.Information("Line {Seq} of {Session} edited", item.Line.Seq, session.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Saving the edit of line {Seq} failed", item.Line.Seq);
            StatusText = $"Правка не сохранена: {ex.Message}";
        }
    }
}

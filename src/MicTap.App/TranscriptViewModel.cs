using System.Collections.ObjectModel;
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
    private readonly CancellationTokenSource _stop = new();
    private readonly TranscriberHost _host;
    private readonly TranscriptionPipeline _pipeline;
    private readonly Task<Func<SpeechDetector>> _detectors;
    private VoiceActivityModel? _vad;
    private SessionInfo? _session;
    private bool _live;

    [ObservableProperty]
    private string _runtimeText = "—";

    [ObservableProperty]
    private bool _runtimeWarning;

    [ObservableProperty]
    private string _statusText = "";

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

    public TranscriptViewModel(ModelStore whisperModel, ModelStore vadModel, Recorder recorder, ILogger logger)
    {
        _whisperModel = whisperModel;
        _vadModel = vadModel;
        _logger = logger.ForContext<TranscriptViewModel>();
        _host = new TranscriberHost(LoadTranscriberAsync, logger, TimeProvider.System);
        _host.StateChanged += (_, state) => _dispatcher.InvokeAsync(() => ShowState(state));
        _detectors = Task.Run(LoadDetectorsAsync);
        _pipeline = new TranscriptionPipeline(_host, _detectors, logger);
        _pipeline.LiveTextChanged += OnLiveText;
        _pipeline.LiveBusyChanged += (_, busy) => _dispatcher.InvokeAsync(() => IsLiveBusy = busy && _live);
        _pipeline.SegmentTranscribed += OnSegmentTranscribed;
        _pipeline.QueueChanged += (_, count) => _dispatcher.InvokeAsync(() => QueueText = count > 0 ? $"ждут распознавания: {count}" : "");
        _pipeline.Failed += OnFailed;
        _pipeline.Attach(recorder);
    }

    public ObservableCollection<TranscriptItem> Items { get; } = [];

    /// <summary>Loads (and on first start downloads) the models. Recording works meanwhile; its speech waits in the queue.</summary>
    public void Start()
    {
        _host.Start();
        _pipeline.Start();
    }

    public void BeginSession(SessionInfo session)
    {
        _session = session;
        _live = true;
        Items.Clear();
        NowText = "";
        IsSilence = false;
    }

    public void EndSession()
    {
        _live = false;
        IsLiveBusy = false;
        NowText = "";
        IsSilence = false;
    }

    /// <summary>
    /// Recognizes <paramref name="session"/> again from its audio; the old transcript is kept as a .bak file.
    /// Sessions recorded before v0.3 are taken as spoken in <paramref name="language"/>.
    /// </summary>
    public async Task RetranscribeAsync(SessionInfo session, string language)
    {
        IsRetranscribing = true;
        _session = session;
        Items.Clear();
        QueueText = "распознаю заново…";
        try
        {
            var retranscriber = new SessionRetranscriber(_host, await _detectors, TimeProvider.System, _logger);
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

    private void OnLiveText(object? sender, LiveTranscript live) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (_live && live.Session == _session)
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

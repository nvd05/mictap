using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;
using Serilog;

namespace MicTap.App;

/// <summary>A chunk's draft text; an edit is saved to transcript.jsonl when the text box loses focus.</summary>
public sealed class TranscriptItem(TranscriptLine line, Action<TranscriptItem> edited) : ObservableObject
{
    private string _text = line.Text;

    public TranscriptLine Line { get; private set; } = line;

    public string TimeText => TimeSpan.FromSeconds(Line.Start).ToString(@"hh\:mm\:ss");

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                Line = Line with { Text = value };
                edited(this);
            }
        }
    }
}

/// <summary>Model download, the recognition runtime, live text and the editable text of finished chunks.</summary>
public sealed partial class TranscriptViewModel : ObservableObject, IDisposable
{
    private readonly ModelStore _models;
    private readonly Recorder _recorder;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private TranscriptionPipeline? _pipeline;
    private SessionInfo? _session;
    private bool _live;
    private volatile string _language = AppSettings.DefaultLanguage;

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
    private bool _isLiveBusy;

    public TranscriptViewModel(ModelStore models, Recorder recorder, ILogger logger)
    {
        _models = models;
        _recorder = recorder;
        _logger = logger.ForContext<TranscriptViewModel>();
    }

    public ObservableCollection<TranscriptItem> Items { get; } = [];

    /// <summary>Whisper language code used by the next recognition pass.</summary>
    public string Language
    {
        get => _language;
        set => _language = value;
    }

    /// <summary>Downloads the model on first start, then loads it. Recording works meanwhile and without it.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            if (!_models.IsReady)
            {
                await DownloadModelAsync();
            }

            RuntimeText = "загрузка…";
            StatusText = "Загружается модель распознавания.";
            ITranscriber transcriber = await Task.Run(() => WhisperTranscriber.Load(_models.ModelPath, _logger));
            _pipeline = new TranscriptionPipeline(transcriber, () => _language, _logger);
            _pipeline.LiveTextChanged += OnLiveText;
            _pipeline.LiveBusyChanged += (_, busy) => _dispatcher.InvokeAsync(() => IsLiveBusy = busy && _live);
            _pipeline.ChunkTranscribed += OnChunkTranscribed;
            _pipeline.Failed += OnFailed;
            _pipeline.Attach(_recorder);
            _pipeline.Start();

            RuntimeText = transcriber.Runtime;
            RuntimeWarning = !transcriber.OnGpu;
            StatusText = transcriber.OnGpu
                ? ""
                : "CUDA не загрузилась — распознавание идёт на CPU, живой текст запаздывает.\n"
                  + "Нужны драйвер NVIDIA 580+ и CUDA Toolkit 13; после установки перезапустите mictap.";
            if (!transcriber.OnGpu)
            {
                _logger.Warning("Recognition runs on the CPU: the CUDA runtime did not load");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Recognition is unavailable");
            IsDownloading = false;
            RuntimeText = "нет";
            RuntimeWarning = true;
            StatusText = $"Распознавание недоступно: {ex.Message}\nЗапись работает.";
        }
    }

    public void BeginSession(SessionInfo session)
    {
        _session = session;
        _live = true;
        Items.Clear();
        NowText = "";
    }

    public void EndSession()
    {
        _live = false;
        IsLiveBusy = false;
        NowText = "";
    }

    public void Dispose() => _pipeline?.Dispose();

    private async Task DownloadModelAsync()
    {
        IsDownloading = true;
        RuntimeText = "скачивание…";
        double gigabytes = _models.Spec.Size / 1e9;
        StatusText = $"Скачивается модель распознавания ({gigabytes:0.0} ГБ) в {Path.GetDirectoryName(_models.ModelPath)}.";
        _logger.Information("Downloading {Model} from {Source}", _models.Spec.FileName, _models.Spec.Source);
        using var http = new HttpClient();
        await _models.DownloadAsync(http, new Progress<double>(done =>
        {
            DownloadPercent = done * 100;
            RuntimeText = $"скачивание {done:P0}";
        }), CancellationToken.None);
        _logger.Information("Model downloaded to {Path}", _models.ModelPath);
        IsDownloading = false;
    }

    private void OnLiveText(object? sender, LiveTranscript live) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (_live && live.Session == _session)
            {
                NowText = live.Text;
            }
        });

    private void OnChunkTranscribed(object? sender, ChunkTranscript chunk) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (chunk.Session == _session)
            {
                Items.Add(new TranscriptItem(chunk.Line, item => SaveEdit(chunk.Session, item)));
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
            _pipeline!.SinkFor(session).Put(item.Line);
            _logger.Information("Text of chunk {Chunk} in {Session} edited", item.Line.Chunk, session.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Saving the edit of chunk {Chunk} failed", item.Line.Chunk);
            StatusText = $"Правка не сохранена: {ex.Message}";
        }
    }
}

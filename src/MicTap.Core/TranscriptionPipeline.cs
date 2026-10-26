using System.Diagnostics;
using System.Threading.Channels;
using Serilog;

namespace MicTap.Core;

public sealed record LiveTranscript(SessionInfo Session, string Text);

public sealed record ChunkTranscript(SessionInfo Session, TranscriptLine Line);

/// <summary>
/// Second consumer of the recorder. Live: a ring of the latest <see cref="LiveWindow"/> of audio is recognized every
/// few seconds and replaces the "now" text. Final: each closed chunk is recognized into transcript.jsonl.
/// The recorder only enqueues, so a slow or failed transcriber never touches recording. After the first error the
/// pipeline stops and reports it through <see cref="Failed"/>.
/// </summary>
public sealed class TranscriptionPipeline : IDisposable
{
    public static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultLiveInterval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string> _language;
    private readonly ILogger _logger;
    private readonly TimeSpan _liveInterval;

    // 200 blocks of 100 ms: twice the window, so a slow pass drops only audio that has left the window anyway.
    private readonly Channel<CapturedAudio> _live = Channel.CreateBounded<CapturedAudio>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly Channel<ChunkCompleted> _finals = Channel.CreateUnbounded<ChunkCompleted>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, JsonlTranscriptSink> _sinks = [];
    private Task _loops = Task.CompletedTask;
    private int _failed;

    public TranscriptionPipeline(ITranscriber transcriber, Func<string> language, ILogger logger)
        : this(transcriber, language, logger, DefaultLiveInterval)
    {
    }

    internal TranscriptionPipeline(ITranscriber transcriber, Func<string> language, ILogger logger, TimeSpan liveInterval)
    {
        Transcriber = transcriber;
        _language = language;
        _logger = logger.ForContext<TranscriptionPipeline>();
        _liveInterval = liveInterval;
    }

    /// <summary>New live text of the latest audio; replaces the previous one.</summary>
    public event EventHandler<LiveTranscript>? LiveTextChanged;

    /// <summary>True while a live pass runs.</summary>
    public event EventHandler<bool>? LiveBusyChanged;

    /// <summary>A chunk's text was written to transcript.jsonl.</summary>
    public event EventHandler<ChunkTranscript>? ChunkTranscribed;

    /// <summary>Raised once: recognition stopped on this error. Recording is not affected.</summary>
    public event EventHandler<Exception>? Failed;

    public ITranscriber Transcriber { get; }

    public void Start() => _loops = Task.WhenAll(Task.Run(LiveLoopAsync), Task.Run(FinalLoopAsync));

    public void Attach(Recorder recorder)
    {
        recorder.AudioCaptured += (_, audio) => Enqueue(audio);
        recorder.ChunkCompleted += (_, chunk) => Enqueue(chunk);
    }

    /// <summary>Capture thread: never blocks; with a full queue the oldest audio is dropped.</summary>
    public void Enqueue(CapturedAudio audio) => _live.Writer.TryWrite(audio);

    public void Enqueue(ChunkCompleted chunk) => _finals.Writer.TryWrite(chunk);

    /// <summary>The transcript of a session; the user's edits go through <see cref="ITranscriptSink.Put"/>.</summary>
    public JsonlTranscriptSink SinkFor(SessionInfo session)
    {
        lock (_gate)
        {
            if (!_sinks.TryGetValue(session.Folder, out JsonlTranscriptSink? sink))
            {
                sink = new JsonlTranscriptSink(session);
                _sinks[session.Folder] = sink;
            }

            return sink;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _live.Writer.TryComplete();
        _finals.Writer.TryComplete();

        // Freeing the model under a running native pass would crash the process; on timeout the process exit frees it.
        if (_loops.Wait(DisposeTimeout))
        {
            Transcriber.Dispose();
        }
        else
        {
            _logger.Warning("Recognition did not stop within {Timeout}; the model is left to the process exit", DisposeTimeout);
        }
    }

    private async Task LiveLoopAsync()
    {
        CancellationToken cancellation = _stop.Token;
        AudioRing ring = AudioRing.For(PcmFormat.Mono16k, LiveWindow);
        SessionInfo? session = null;
        long recognized = 0;
        using var timer = new PeriodicTimer(_liveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                while (_live.Reader.TryRead(out CapturedAudio? audio))
                {
                    if (audio.Session != session)
                    {
                        session = audio.Session;
                        ring.Clear();
                        recognized = 0;
                    }

                    ring.Write(audio.Pcm.Span);
                }

                if (session is null || ring.Written == recognized)
                {
                    continue; // nothing new: not recording
                }

                recognized = ring.Written;
                LiveBusyChanged?.Invoke(this, true);
                long started = Stopwatch.GetTimestamp();
                float[] window = ring.Snapshot();
                IReadOnlyList<TranscriptSegment> segments =
                    await Transcriber.TranscribeAsync(window, _language(), cancellation);
                LiveBusyChanged?.Invoke(this, false);
                string text = Join(segments);
                _logger.Debug(
                    "Live pass: {Audio:0.0} s of audio in {Elapsed:0.0} s, {Chars} chars",
                    window.Length / (double)PcmFormat.Mono16k.SampleRate, Stopwatch.GetElapsedTime(started).TotalSeconds, text.Length);
                LiveTextChanged?.Invoke(this, new LiveTranscript(session, text));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private async Task FinalLoopAsync()
    {
        CancellationToken cancellation = _stop.Token;
        try
        {
            await foreach (ChunkCompleted done in _finals.Reader.ReadAllAsync(cancellation))
            {
                if (done.Chunk.DataBytes == 0)
                {
                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                float[] samples = WavSamples.Read(done.Chunk.Path);
                IReadOnlyList<TranscriptSegment> segments =
                    await Transcriber.TranscribeAsync(samples, _language(), cancellation);
                TimeSpan end = done.Start + PcmFormat.Mono16k.DurationOf(done.Chunk.DataBytes);
                var line = new TranscriptLine(done.Chunk.Index, Seconds(done.Start), Seconds(end), Join(segments));
                SinkFor(done.Session).Put(line);
                _logger.Information(
                    "Chunk {Chunk} of {Session} transcribed in {Elapsed:0.0} s",
                    done.Chunk.Index, done.Session.Name, Stopwatch.GetElapsedTime(started).TotalSeconds);
                ChunkTranscribed?.Invoke(this, new ChunkTranscript(done.Session, line));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        _logger.Error(error, "Recognition stopped; recording goes on");
        _stop.Cancel();
        _live.Writer.TryComplete();
        _finals.Writer.TryComplete();
        Failed?.Invoke(this, error);
    }

    private static string Join(IEnumerable<TranscriptSegment> segments) =>
        string.Join(" ", segments.Select(s => s.Text).Where(t => t.Length > 0));

    private static double Seconds(TimeSpan time) => Math.Round(time.TotalSeconds, 2);
}

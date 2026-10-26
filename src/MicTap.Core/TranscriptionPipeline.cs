using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Serilog;

namespace MicTap.Core;

/// <summary>
/// Live text of the speech not yet in the transcript; <paramref name="Silence"/> when the VAD heard no speech there.
/// </summary>
public sealed record LiveTranscript(SessionInfo Session, string Text, bool Silence);

public sealed record SegmentTranscript(SessionInfo Session, TranscriptLine Line);

/// <summary>
/// Second consumer of the recorder. Final text: captured audio goes through the VAD into phrases, each is recognized
/// in the language of its start and appended to transcript.jsonl; silence never reaches Whisper.
/// Live text: every few seconds, and at once after a phrase gets its final text, the audio after the last finished
/// phrase is recognized when it holds speech, so the draft never repeats what the transcript already has.
/// The recorder only enqueues, so recognition never blocks recording. A failed transcriber is reloaded by the
/// <see cref="TranscriberHost"/> and the segment is retried; segments wait in the queue meanwhile. Any other error
/// stops the pipeline and is reported through <see cref="Failed"/>.
/// </summary>
public sealed class TranscriptionPipeline : IDisposable
{
    /// <summary>The longest phrase and a little more: the draft can cover a whole phrase that is still being spoken.</summary>
    public static readonly TimeSpan LiveWindow = SpeechSegmenter.MaxSegment + TimeSpan.FromSeconds(2);

    public static readonly TimeSpan DefaultLiveInterval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    private readonly TranscriberHost _host;
    private readonly Task<Func<SpeechDetector>> _detectors;
    private readonly ILogger _logger;
    private readonly TimeSpan _liveInterval;

    // Every captured block and mark, in order: the segmenter must see all audio.
    private readonly Channel<object> _stream = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });

    // Blocks of 100 ms for twice the live window, so a slow pass drops only audio that has left the window anyway.
    private readonly Channel<CapturedAudio> _live = Channel.CreateBounded<CapturedAudio>(
        new BoundedChannelOptions((int)(LiveWindow.TotalSeconds * 20)) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // A phrase got its final text: the draft is recognized again at once, without it.
    private readonly Channel<bool> _liveWake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly Channel<PendingSegment> _segments = Channel.CreateUnbounded<PendingSegment>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, JsonlTranscriptSink> _sinks = [];
    private readonly ConcurrentDictionary<string, ChunkJanitor> _janitors = new();
    private LiveLanguage? _liveLanguage;
    private Finished? _finished;
    private Task _loops = Task.CompletedTask;
    private int _pending;
    private int _failed;

    public TranscriptionPipeline(TranscriberHost host, Task<Func<SpeechDetector>> detectors, ILogger logger)
        : this(host, detectors, logger, DefaultLiveInterval)
    {
    }

    internal TranscriptionPipeline(TranscriberHost host, Task<Func<SpeechDetector>> detectors, ILogger logger, TimeSpan liveInterval)
    {
        _host = host;
        _detectors = detectors;
        _logger = logger.ForContext<TranscriptionPipeline>();
        _liveInterval = liveInterval;
    }

    /// <summary>New live text of the speech not yet in the transcript; replaces the previous one.</summary>
    public event EventHandler<LiveTranscript>? LiveTextChanged;

    /// <summary>True while a live pass runs.</summary>
    public event EventHandler<bool>? LiveBusyChanged;

    /// <summary>A segment's text was appended to transcript.jsonl.</summary>
    public event EventHandler<SegmentTranscript>? SegmentTranscribed;

    /// <summary>Number of speech segments waiting for recognition.</summary>
    public event EventHandler<int>? QueueChanged;

    /// <summary>Raised once: recognition stopped on this error. Recording is not affected.</summary>
    public event EventHandler<Exception>? Failed;

    public void Start() => _loops = Task.WhenAll(Task.Run(SegmentLoopAsync), Task.Run(FinalLoopAsync), Task.Run(LiveLoopAsync));

    public void Attach(Recorder recorder)
    {
        recorder.Started += (_, started) => Enqueue(started);
        recorder.AudioCaptured += (_, audio) => Enqueue(audio);
        recorder.LanguageMarked += (_, marked) => Enqueue(marked);
        recorder.ChunkCompleted += (_, chunk) => OnChunkCompleted(chunk);
        recorder.Stopped += (_, stopped) => EnqueueEnd(stopped.Session);
    }

    /// <summary>A recording begins: its segments start at its offset, in its language; its retention and phrase pause apply.</summary>
    public void Enqueue(RecordingStarted started)
    {
        if (started.Retention == AudioRetention.DeleteAfterTranscript)
        {
            _janitors[ started.Session.Folder ] = new ChunkJanitor(_logger);
        }
        else
        {
            _janitors.TryRemove(started.Session.Folder, out _);
        }

        _stream.Writer.TryWrite(started);
        Volatile.Write(ref _finished, new Finished(started.Session, started.Offset));
        Volatile.Write(ref _liveLanguage, new LiveLanguage(started.Session, started.Language));
    }

    /// <summary>Capture thread: never blocks.</summary>
    public void Enqueue(CapturedAudio audio)
    {
        _stream.Writer.TryWrite(audio);
        _live.Writer.TryWrite(audio);
    }

    public void Enqueue(LanguageMarked marked)
    {
        _stream.Writer.TryWrite(marked);
        Volatile.Write(ref _liveLanguage, new LiveLanguage(marked.Session, marked.Mark.Lang));
    }

    /// <summary>Writer thread: a closed chunk may be deleted once its speech is written (retention deleteAfterTranscript).</summary>
    public void OnChunkCompleted(ChunkCompleted chunk) =>
        _janitors.GetValueOrDefault(chunk.Session.Folder)?.ChunkClosed(chunk.Chunk.Path, chunk.End);

    /// <summary>All audio of the session has been enqueued: its last speech becomes a segment.</summary>
    public void EnqueueEnd(SessionInfo session) => _stream.Writer.TryWrite(new SessionEnd(session));

    /// <summary>The transcript of a session; the user's edits go through <see cref="ITranscriptSink.Edit"/>.</summary>
    public JsonlTranscriptSink SinkFor(SessionInfo session)
    {
        lock (_gate)
        {
            if (!_sinks.TryGetValue(session.Folder, out JsonlTranscriptSink? sink))
            {
                sink = new JsonlTranscriptSink(session.Folder);
                _sinks[ session.Folder ] = sink;
            }

            return sink;
        }
    }

    /// <summary>The session's transcript.jsonl was replaced on disk: read it afresh on next use.</summary>
    public void ForgetSink(SessionInfo session)
    {
        lock (_gate)
        {
            _sinks.Remove(session.Folder);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stream.Writer.TryComplete();
        _live.Writer.TryComplete();
        _segments.Writer.TryComplete();
        if (!_loops.Wait(DisposeTimeout))
        {
            _logger.Warning("Recognition did not stop within {Timeout}", DisposeTimeout);
        }
    }

    private async Task SegmentLoopAsync()
    {
        CancellationToken cancellation = _stop.Token;
        try
        {
            SpeechDetector detect = (await _detectors.WaitAsync(cancellation))();
            SpeechSegmenter? segmenter = null;
            SessionInfo? session = null;
            ChunkJanitor? janitor = null;
            await foreach (object item in _stream.Reader.ReadAllAsync(cancellation))
            {
                switch (item)
                {
                    case RecordingStarted started:
                        session = started.Session;
                        segmenter = new SpeechSegmenter(detect, started.Language, started.Offset, started.PhrasePause);
                        janitor = _janitors.GetValueOrDefault(session.Folder);
                        break;
                    case LanguageMarked marked when marked.Session == session:
                        Queue(session, segmenter!.Mark(TimeSpan.FromSeconds(marked.Mark.At), marked.Mark.Lang), segmenter, janitor);
                        break;
                    case CapturedAudio audio when audio.Session == session:
                        Queue(session, segmenter!.Add(WavSamples.FromPcm16(audio.Pcm.Span)), segmenter, janitor);
                        break;
                    case SessionEnd end when end.Session == session:
                        Queue(session, segmenter!.Flush(), segmenter, janitor);
                        janitor = null;
                        session = null;
                        segmenter = null;
                        break;
                }
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
            await foreach (PendingSegment pending in _segments.Reader.ReadAllAsync(cancellation))
            {
                SpeechSegment segment = pending.Segment;
                long started = Stopwatch.GetTimestamp();
                IReadOnlyList<TranscriptSegment> recognized;
                while (true)
                {
                    try
                    {
                        recognized = await _host.TranscribeAsync(segment.Samples, segment.Language, cancellation);
                        break;
                    }
                    catch (TranscriberFailedException)
                    {
                        // The host loads a fresh transcriber; the same segment goes again.
                    }
                }

                QueueChanged?.Invoke(this, Interlocked.Decrement(ref _pending));
                string text = Join(recognized);
                _logger.Debug(
                    "Segment {Start:0.00}-{End:0.00} s ({Language}) recognized in {Elapsed:0.0} s, {Chars} chars",
                    segment.Start.TotalSeconds, segment.End.TotalSeconds, segment.Language,
                    Stopwatch.GetElapsedTime(started).TotalSeconds, text.Length);
                if (WhisperArtifacts.IsArtifact(text))
                {
                    _logger.Information(
                        "Segment {Start:0.00}-{End:0.00} s left out: Whisper made up «{Text}»",
                        segment.Start.TotalSeconds, segment.End.TotalSeconds, text);
                }
                else if (text.Length > 0) // empty: the VAD heard a sound Whisper did not take for words
                {
                    TranscriptLine line = SinkFor(pending.Session).Append(
                        Seconds(segment.Start), Seconds(segment.End), segment.Language, text);
                    SegmentTranscribed?.Invoke(this, new SegmentTranscript(pending.Session, line));
                }

                Volatile.Write(ref _finished, new Finished(pending.Session, segment.End));
                _liveWake.Writer.TryWrite(true);
                pending.Janitor?.Written();
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

    private async Task LiveLoopAsync()
    {
        CancellationToken cancellation = _stop.Token;
        try
        {
            SpeechDetector detect = (await _detectors.WaitAsync(cancellation))();
            PcmFormat format = PcmFormat.Mono16k;
            AudioRing ring = AudioRing.For(format, LiveWindow);
            TimeSpan ringEnd = TimeSpan.Zero;
            SessionInfo? session = null;
            LiveLanguage? language = null;
            (TimeSpan From, TimeSpan To) recognized = default;
            while (true)
            {
                await NextLivePassAsync(cancellation);
                LiveLanguage? latest = Volatile.Read(ref _liveLanguage);
                if (!ReferenceEquals(latest, language) && latest is not null)
                {
                    // A new recording or a new language: what was said before belongs to the old one.
                    language = latest;
                    ring.Clear();
                    if (latest.Session == session)
                    {
                        LiveTextChanged?.Invoke(this, new LiveTranscript(session, "", Silence: false));
                    }
                }

                while (_live.Reader.TryRead(out CapturedAudio? audio))
                {
                    if (audio.Session != session || audio.At != ringEnd)
                    {
                        // Another recording, or blocks dropped while a pass was slow: the ring starts over here.
                        session = audio.Session;
                        ring.Clear();
                    }

                    ring.Write(audio.Pcm.Span);
                    ringEnd = audio.At + format.DurationOf(audio.Pcm.Length);
                }

                if (session is null || language is null)
                {
                    continue; // not recording
                }

                // Only speech after the last finished phrase: what the transcript has is not repeated.
                TimeSpan ringStart = ringEnd - format.DurationOf(ring.Count * (long)format.BlockAlign);
                TimeSpan from = Volatile.Read(ref _finished) is { } finished && finished.Session == session && finished.At > ringStart
                    ? finished.At
                    : ringStart;
                if (recognized == (from, ringEnd))
                {
                    continue; // nothing new
                }

                recognized = (from, ringEnd);
                float[] samples = ring.Snapshot();
                float[] window = samples[ (int)Math.Min(samples.Length, (from - ringStart).Ticks * format.SampleRate / TimeSpan.TicksPerSecond).. ];
                if (window.Length == 0 || detect(window).Count == 0)
                {
                    LiveTextChanged?.Invoke(this, new LiveTranscript(session, "", Silence: true));
                    continue;
                }

                if (!_host.IsReady)
                {
                    continue;
                }

                LiveBusyChanged?.Invoke(this, true);
                long started = Stopwatch.GetTimestamp();
                try
                {
                    IReadOnlyList<TranscriptSegment> segments = await _host.TranscribeAsync(window, language.Language, cancellation);
                    _logger.Debug(
                        "Live pass: {Audio:0.0} s in {Elapsed:0.0} s",
                        window.Length / (double)PcmFormat.Mono16k.SampleRate, Stopwatch.GetElapsedTime(started).TotalSeconds);
                    string draft = Join(segments);
                    LiveTextChanged?.Invoke(this, new LiveTranscript(session, WhisperArtifacts.IsArtifact(draft) ? "" : draft, Silence: false));
                }
                catch (TranscriberFailedException)
                {
                    // The host reports and recovers; live text resumes with the next pass.
                }
                finally
                {
                    LiveBusyChanged?.Invoke(this, false);
                }
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

    /// <summary>Waits for the next live pass: the interval, or less when a phrase has just got its final text.</summary>
    private async Task NextLivePassAsync(CancellationToken cancellation)
    {
        using var interval = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        interval.CancelAfter(_liveInterval);
        try
        {
            await _liveWake.Reader.ReadAsync(interval.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // The interval is over: a regular pass.
        }
    }

    private void Queue(SessionInfo session, IReadOnlyList<SpeechSegment> segments, SpeechSegmenter segmenter, ChunkJanitor? janitor)
    {
        janitor?.Segmented(segments, segmenter.Committed);
        foreach (SpeechSegment segment in segments)
        {
            _segments.Writer.TryWrite(new PendingSegment(session, segment, janitor));
            QueueChanged?.Invoke(this, Interlocked.Increment(ref _pending));
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
        _stream.Writer.TryComplete();
        _live.Writer.TryComplete();
        _segments.Writer.TryComplete();
        Failed?.Invoke(this, error);
    }

    private static string Join(IEnumerable<TranscriptSegment> segments) =>
        string.Join(" ", segments.Select(s => s.Text).Where(t => t.Length > 0));

    private static double Seconds(TimeSpan time) => Math.Round(time.TotalSeconds, 2);

    private sealed record SessionEnd(SessionInfo Session);

    private sealed record PendingSegment(SessionInfo Session, SpeechSegment Segment, ChunkJanitor? Janitor);

    private sealed record LiveLanguage(SessionInfo Session, string Language);

    /// <summary>The session's audio before <paramref name="At"/> is in the transcript, or held no speech.</summary>
    private sealed record Finished(SessionInfo Session, TimeSpan At);
}

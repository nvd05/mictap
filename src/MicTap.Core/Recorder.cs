using System.Threading.Channels;
using Serilog;

namespace MicTap.Core;

public sealed record RecordingOptions(string RootFolder, string DeviceName, int ChunkSeconds, string AppVersion, string Language);

public enum StopReason
{
    /// <summary>Stop was requested.</summary>
    Requested,

    /// <summary>The capture device reported a failure or ended capture on its own.</summary>
    DeviceFailed,

    /// <summary>The capture device delivered no audio for <see cref="Recorder.SilenceTimeout"/>.</summary>
    DeviceSilent,

    /// <summary>Audio could not be written to disk.</summary>
    WriteFailed,

    /// <summary>The disk fell behind by more than the write buffer: recording stopped instead of dropping audio.</summary>
    WriteStalled,
}

public sealed record RecordingProgress(SessionInfo Session, int ChunkIndex, TimeSpan Recorded);

public sealed record RecordingStopped(SessionInfo Session, StopReason Reason, Exception? Error, TimeSpan Recorded);

/// <summary>A copy of one captured block of 16 kHz mono 16-bit PCM; shared by consumers, never modified.</summary>
public sealed record CapturedAudio(SessionInfo Session, ReadOnlyMemory<byte> Pcm);

public sealed record LanguageMarked(SessionInfo Session, LanguageMark Mark);

/// <summary>A closed chunk; <paramref name="Start"/> is its offset from the session start.</summary>
public sealed record ChunkCompleted(SessionInfo Session, ChunkInfo Chunk, TimeSpan Start);

/// <summary>
/// Records one session at a time. The capture thread only copies each buffer into a channel; a
/// <see cref="ChunkWriterStage"/> writes the chunks on its own thread. Up to the write buffer of audio may wait for
/// the disk; beyond that recording stops with <see cref="StopReason.WriteStalled"/>.
/// <see cref="LevelChanged"/> is raised on the capture thread, the other events on the writer thread.
/// </summary>
public sealed class Recorder
{
    /// <summary>A device that delivers no audio for this long is treated as lost.</summary>
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Audio that may wait for the disk before recording stops.</summary>
    public static readonly TimeSpan DefaultWriteBuffer = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _writeBuffer;
    private readonly Lock _gate = new(); // guards starting and finishing sessions, never taken on the capture path
    private Session? _active;

    public Recorder(ILogger logger, TimeProvider time)
        : this(logger, time, DefaultWriteBuffer)
    {
    }

    internal Recorder(ILogger logger, TimeProvider time, TimeSpan writeBuffer)
    {
        _logger = logger.ForContext<Recorder>();
        _time = time;
        _writeBuffer = writeBuffer;
    }

    /// <summary>Peak level of the latest captured block, 0..1.</summary>
    public event EventHandler<double>? LevelChanged;

    public event EventHandler<RecordingProgress>? ProgressChanged;

    /// <summary>
    /// The block that went to the disk channel, for a second consumer (live text). Capture thread: handlers only enqueue.
    /// </summary>
    public event EventHandler<CapturedAudio>? AudioCaptured;

    /// <summary>A chunk got its final header and will not change. Writer thread: handlers only enqueue.</summary>
    public event EventHandler<ChunkCompleted>? ChunkCompleted;

    /// <summary>Raised once per started session, after the last queued audio reached its chunk and the chunk got its final header.</summary>
    public event EventHandler<RecordingStopped>? Stopped;

    /// <summary>
    /// The language of speech from a point of the session on: raised at start with the session's language (at 0)
    /// and by <see cref="MarkLanguage"/>. Raised on the thread that started or marked.
    /// </summary>
    public event EventHandler<LanguageMarked>? LanguageMarked;

    /// <summary>True from start until the session has finished writing.</summary>
    public bool IsRecording => Volatile.Read(ref _active) is not null;

    /// <summary>
    /// Marks that speech is in <paramref name="language"/> from the audio captured so far on, and stores the mark in
    /// meta.json. Returns null when no recording runs.
    /// </summary>
    public LanguageMark? MarkLanguage(string language)
    {
        LanguageMarked marked;
        lock (_gate)
        {
            Session? session = _active;
            if (session is null || session.IsEnding)
            {
                return null;
            }

            TimeSpan at = PcmFormat.Mono16k.DurationOf(Interlocked.Read(ref session.CapturedBytes));
            var mark = new LanguageMark(Math.Round(at.TotalSeconds, 3), language);
            session.Meta = session.Meta with { LanguageMarks = [.. session.Meta.LanguageMarks, mark] };
            session.Meta.WriteTo(session.Info);
            marked = new LanguageMarked(session.Info, mark);
        }

        _logger.Information("Language {Language} from {At} s of {Session}", marked.Mark.Lang, marked.Mark.At, marked.Session.Name);
        LanguageMarked?.Invoke(this, marked);
        return marked.Mark;
    }

    /// <summary>Test hook: runs on the writer thread before each block is written.</summary>
    internal Action? BeforeDiskWrite { get; set; }

    /// <summary>
    /// Creates the session folder, meta.json and the first chunk, then starts <paramref name="source"/>.
    /// The recorder owns the source from here on and disposes it once capture has ended.
    /// </summary>
    public SessionInfo Start(RecordingOptions options, IAudioSource source)
    {
        Session session;
        try
        {
            session = CreateSession(options, source);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        LanguageMarked?.Invoke(this, new LanguageMarked(session.Info, session.Meta.LanguageMarks[0]));
        source.DataAvailable += OnData;
        source.Stopped += OnSourceStopped;
        session.Timer = _time.CreateTimer(_ => Tick(), null, WatchdogInterval, WatchdogInterval);
        session.Stage.Start();
        try
        {
            source.Start();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Capture did not start for session {Session}", session.Info.Name);
            source.Stopped -= OnSourceStopped;
            session.Notify = false;
            End(session, StopReason.DeviceFailed, ex);
            source.Dispose();
            session.Finished.Task.Wait(); // the writer closes the empty first chunk
            throw;
        }

        _logger.Information(
            "Session {Session} started on {Device}, {ChunkSeconds} s chunks, in {Folder}",
            session.Info.Name, options.DeviceName, options.ChunkSeconds, session.Info.Folder);
        return session.Info;
    }

    /// <summary>Stops capture and completes once everything captured is on disk with final chunk headers.</summary>
    public async Task StopAsync()
    {
        Session? session = Volatile.Read(ref _active);
        if (session is null)
        {
            return;
        }

        session.StopRequested = true;
        try
        {
            session.Source.Stop();
        }
        catch (AudioCaptureException ex)
        {
            End(session, StopReason.DeviceFailed, ex);
        }

        try
        {
            await session.Ended.Task.WaitAsync(StopTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.Warning("Capture did not confirm stop within {Timeout}; closing the session anyway", StopTimeout);
            End(session, StopReason.Requested, null);
        }

        await session.Finished.Task.ConfigureAwait(false);
    }

    /// <summary>Detects a device that stopped delivering audio; runs every <see cref="WatchdogInterval"/>.</summary>
    internal void Tick()
    {
        Session? session = Volatile.Read(ref _active);
        if (session is not null && !session.IsEnding
            && _time.GetElapsedTime(Volatile.Read(ref session.LastDataAt)) >= SilenceTimeout)
        {
            End(session, StopReason.DeviceSilent, null);
        }
    }

    private Session CreateSession(RecordingOptions options, IAudioSource source)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException("A recording is already running.");
            }

            PcmFormat format = PcmFormat.Mono16k;
            DateTimeOffset started = _time.GetLocalNow();
            SessionInfo info = SessionFolders.Create(options.RootFolder, started);
            var meta = new SessionMeta(
                [new SessionStart(started, 0)], options.DeviceName, MetaFormat.From(format), options.ChunkSeconds,
                options.AppVersion, [new LanguageMark(0, options.Language)]);
            meta.WriteTo(info);
            var writer = new ChunkedWavWriter(info.AudioFolder, format, options.ChunkSeconds);
            writer.ChunkClosed += (_, chunk) =>
            {
                _logger.Information(
                    "Chunk {Chunk} closed: {Seconds:0.0} s", chunk.Path, format.DurationOf(chunk.DataBytes).TotalSeconds);
                ChunkCompleted?.Invoke(
                    this, new ChunkCompleted(info, chunk, TimeSpan.FromSeconds((double)chunk.Index * options.ChunkSeconds)));
            };

            Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var stage = new ChunkWriterStage(writer, channel.Reader, _time, BeforeDiskWrite);
            long writeBufferBytes = (long)(format.ByteRate * _writeBuffer.TotalSeconds);
            var session = new Session(info, meta, source, writer, channel, stage, writeBufferBytes, _time.GetTimestamp());

            stage.Written += bytes =>
            {
                Interlocked.Add(ref session.QueuedBytes, -bytes);
                ProgressChanged?.Invoke(this, new RecordingProgress(info, writer.CurrentIndex, format.DurationOf(writer.TotalBytes)));
            };
            stage.Completion.ContinueWith(task => OnWriterDone(session, task.Result), TaskScheduler.Default);

            _active = session;
            return session;
        }
    }

    private void OnData(object? sender, AudioBuffer buffer)
    {
        Session? session = Volatile.Read(ref _active);
        if (session is null || session.IsEnding || !ReferenceEquals(session.Source, sender))
        {
            return;
        }

        // NAudio reuses the buffer once this handler returns, so the channel gets a copy.
        byte[] block = buffer.Span.ToArray();
        if (Interlocked.Add(ref session.QueuedBytes, block.Length) > session.WriteBufferBytes)
        {
            End(session, StopReason.WriteStalled, null);
            return;
        }

        Interlocked.Add(ref session.CapturedBytes, block.Length);
        session.Channel.Writer.TryWrite(block);
        Volatile.Write(ref session.LastDataAt, _time.GetTimestamp());
        LevelChanged?.Invoke(this, AudioLevel.Peak(block));
        AudioCaptured?.Invoke(this, new CapturedAudio(session.Info, block));
    }

    private void OnSourceStopped(object? sender, CaptureStopped e)
    {
        var source = (IAudioSource)sender!;
        source.DataAvailable -= OnData;
        source.Stopped -= OnSourceStopped;

        Session? session = Volatile.Read(ref _active);
        if (session is not null && ReferenceEquals(session.Source, source))
        {
            session.SourceStopped = true;
            StopReason reason = e.Error is null && session.StopRequested ? StopReason.Requested : StopReason.DeviceFailed;
            End(session, reason, e.Error);
        }

        // Capture has ended, so releasing the device from its own callback is safe.
        source.Dispose();
    }

    /// <summary>Stops taking audio: the writer drains what is queued, then the session finishes.</summary>
    private void End(Session session, StopReason reason, Exception? error)
    {
        if (Interlocked.CompareExchange(ref session.Ending, 1, 0) != 0)
        {
            return;
        }

        session.Reason = reason;
        session.Error = error;
        session.Timer?.Dispose();
        session.Source.DataAvailable -= OnData;
        if (reason is not (StopReason.Requested or StopReason.DeviceFailed) && !session.SourceStopped)
        {
            // Capture still runs: ask it to end; the source is disposed when it reports Stopped.
            try
            {
                session.Source.Stop();
            }
            catch (AudioCaptureException ex)
            {
                _logger.Error(ex, "Capture device did not accept stop and stays open");
            }
        }

        session.Channel.Writer.TryComplete();
        session.Ended.TrySetResult();
    }

    private void OnWriterDone(Session session, Exception? failure)
    {
        if (failure is not null)
        {
            _logger.Error(failure, "Writing session {Session} failed", session.Info.Name);
            End(session, StopReason.WriteFailed, failure);
            if (session.Reason == StopReason.Requested)
            {
                session.Reason = StopReason.WriteFailed;
                session.Error = failure;
            }
        }

        lock (_gate)
        {
            if (_active == session)
            {
                _active = null;
            }
        }

        TimeSpan recorded = PcmFormat.Mono16k.DurationOf(session.Writer.TotalBytes);
        if (session.Reason == StopReason.Requested)
        {
            _logger.Information("Session {Session} stopped, {Recorded} recorded", session.Info.Name, recorded);
        }
        else
        {
            _logger.Error(session.Error, "Session {Session} stopped: {Reason}, {Recorded} recorded", session.Info.Name, session.Reason, recorded);
        }

        if (session.Notify)
        {
            Stopped?.Invoke(this, new RecordingStopped(session.Info, session.Reason, session.Error, recorded));
        }

        session.Finished.TrySetResult();
    }

    private sealed class Session(
        SessionInfo info, SessionMeta meta, IAudioSource source, ChunkedWavWriter writer, Channel<byte[]> channel,
        ChunkWriterStage stage, long writeBufferBytes, long startedAt)
    {
        public readonly SessionInfo Info = info;
        public readonly IAudioSource Source = source;
        public readonly ChunkedWavWriter Writer = writer;
        public readonly Channel<byte[]> Channel = channel;
        public readonly ChunkWriterStage Stage = stage;
        public readonly long WriteBufferBytes = writeBufferBytes;
        public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long CapturedBytes;
        public long QueuedBytes;
        public long LastDataAt = startedAt;
        public int Ending;
        public volatile bool StopRequested;
        public volatile bool SourceStopped;
        public bool Notify = true;
        public StopReason Reason;
        public Exception? Error;
        public ITimer? Timer;
        public SessionMeta Meta = meta;

        public bool IsEnding => Volatile.Read(ref Ending) != 0;
    }
}

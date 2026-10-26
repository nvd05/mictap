using Serilog;

namespace MicTap.Core;

public sealed record RecordingOptions(string RootFolder, string DeviceName, int ChunkSeconds, string AppVersion);

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
}

public sealed record RecordingProgress(SessionInfo Session, int ChunkIndex, TimeSpan Recorded);

public sealed record RecordingStopped(SessionInfo Session, StopReason Reason, Exception? Error, TimeSpan Recorded);

/// <summary>
/// Records one session at a time: audio source → chunked WAV files plus meta.json. Written audio is flushed
/// to disk every <see cref="FlushInterval"/>. Events are raised on capture or timer threads.
/// </summary>
public sealed class Recorder
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>A device that delivers no audio for this long is treated as lost.</summary>
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Session? _active;

    public Recorder(ILogger logger, TimeProvider time)
    {
        _logger = logger.ForContext<Recorder>();
        _time = time;
    }

    /// <summary>Peak level of the latest captured block, 0..1.</summary>
    public event EventHandler<double>? LevelChanged;

    public event EventHandler<RecordingProgress>? ProgressChanged;

    /// <summary>Raised once per started session, after its last chunk got the final header.</summary>
    public event EventHandler<RecordingStopped>? Stopped;

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

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

        source.DataAvailable += OnData;
        source.Stopped += OnSourceStopped;
        try
        {
            source.Start();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Capture did not start for session {Session}", session.Info.Name);
            source.Stopped -= OnSourceStopped;
            Detach(session);
            session.Writer.Dispose();
            source.Dispose();
            throw;
        }

        _logger.Information(
            "Session {Session} started on {Device}, {ChunkSeconds} s chunks, in {Folder}",
            session.Info.Name, options.DeviceName, options.ChunkSeconds, session.Info.Folder);
        return session.Info;
    }

    /// <summary>Stops capture and completes once the last chunk got its final header.</summary>
    public async Task StopAsync()
    {
        Session? session;
        lock (_gate)
        {
            session = _active;
            if (session is null)
            {
                return;
            }

            session.StopRequested = true;
        }

        try
        {
            session.Source.Stop();
        }
        catch (AudioCaptureException ex)
        {
            Finish(session, StopReason.DeviceFailed, ex);
            return;
        }

        try
        {
            await session.Finished.Task.WaitAsync(StopTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.Warning("Capture did not confirm stop within {Timeout}; closing the session anyway", StopTimeout);
            Finish(session, StopReason.Requested, null);
        }
    }

    /// <summary>Flushes written audio to disk and detects a silent device; runs every <see cref="FlushInterval"/>.</summary>
    internal void Tick()
    {
        Session? session;
        Exception? failure = null;
        bool silent;
        lock (_gate)
        {
            session = _active;
            if (session is null)
            {
                return;
            }

            try
            {
                session.Writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }

            silent = _time.GetElapsedTime(session.LastDataAt) >= SilenceTimeout;
        }

        if (failure is not null)
        {
            Finish(session, StopReason.WriteFailed, failure);
        }
        else if (silent)
        {
            Finish(session, StopReason.DeviceSilent, null);
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
            new SessionMeta(started, options.DeviceName, MetaFormat.From(format), options.ChunkSeconds, options.AppVersion)
                .WriteTo(info);
            var writer = new ChunkedWavWriter(info.AudioFolder, format, options.ChunkSeconds);
            writer.ChunkClosed += (_, chunk) => _logger.Information(
                "Chunk {Chunk} closed: {Seconds:0.0} s", chunk.Path, format.DurationOf(chunk.DataBytes).TotalSeconds);

            var session = new Session(info, source, writer, _time.GetTimestamp());
            session.Timer = _time.CreateTimer(_ => Tick(), null, FlushInterval, FlushInterval);
            _active = session;
            return session;
        }
    }

    private void OnData(object? sender, AudioBuffer buffer)
    {
        Session? session;
        RecordingProgress progress;
        Exception? failure = null;
        lock (_gate)
        {
            session = _active;
            if (session is null || !ReferenceEquals(session.Source, sender))
            {
                return;
            }

            try
            {
                session.Writer.Write(buffer.Span);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }

            session.LastDataAt = _time.GetTimestamp();
            progress = new RecordingProgress(
                session.Info, session.Writer.CurrentIndex, PcmFormat.Mono16k.DurationOf(session.Writer.TotalBytes));
        }

        if (failure is not null)
        {
            Finish(session, StopReason.WriteFailed, failure);
            return;
        }

        LevelChanged?.Invoke(this, AudioLevel.Peak(buffer.Span));
        ProgressChanged?.Invoke(this, progress);
    }

    private void OnSourceStopped(object? sender, CaptureStopped e)
    {
        var source = (IAudioSource)sender!;
        source.DataAvailable -= OnData;
        source.Stopped -= OnSourceStopped;

        Session? session;
        lock (_gate)
        {
            session = _active is not null && ReferenceEquals(_active.Source, source) ? _active : null;
        }

        if (session is not null)
        {
            session.SourceStopped = true;
            StopReason reason = e.Error is null && session.StopRequested ? StopReason.Requested : StopReason.DeviceFailed;
            Finish(session, reason, e.Error);
        }

        // Capture has ended, so releasing the device from its own callback is safe.
        source.Dispose();
    }

    private void Finish(Session session, StopReason reason, Exception? error)
    {
        if (!Detach(session))
        {
            return;
        }

        try
        {
            session.Writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The chunk keeps zero sizes in its header and gets repaired on the next start.
            _logger.Error(ex, "Closing the last chunk of session {Session} failed", session.Info.Name);
            if (reason == StopReason.Requested)
            {
                reason = StopReason.WriteFailed;
                error = ex;
            }
        }

        if (reason is (StopReason.WriteFailed or StopReason.DeviceSilent) && !session.SourceStopped)
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

        TimeSpan recorded = PcmFormat.Mono16k.DurationOf(session.Writer.TotalBytes);
        if (reason == StopReason.Requested)
        {
            _logger.Information("Session {Session} stopped, {Recorded} recorded", session.Info.Name, recorded);
        }
        else
        {
            _logger.Error(error, "Session {Session} stopped: {Reason}, {Recorded} recorded", session.Info.Name, reason, recorded);
        }

        session.Finished.TrySetResult();
        Stopped?.Invoke(this, new RecordingStopped(session.Info, reason, error, recorded));
    }

    /// <summary>Makes <paramref name="session"/> inactive; false if it already was.</summary>
    private bool Detach(Session session)
    {
        lock (_gate)
        {
            if (_active != session)
            {
                return false;
            }

            _active = null;
        }

        session.Timer?.Dispose();
        session.Source.DataAvailable -= OnData;
        return true;
    }

    private sealed class Session(SessionInfo info, IAudioSource source, ChunkedWavWriter writer, long startedAt)
    {
        public SessionInfo Info { get; } = info;

        public IAudioSource Source { get; } = source;

        public ChunkedWavWriter Writer { get; } = writer;

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ITimer? Timer { get; set; }

        public long LastDataAt { get; set; } = startedAt;

        public bool StopRequested { get; set; }

        public bool SourceStopped { get; set; }
    }
}

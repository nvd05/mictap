using Serilog;

namespace MicTap.Core;

public enum TranscriberStatus
{
    Loading,
    Ready,
    Failed,
}

/// <summary>What the host is doing; <paramref name="RetryIn"/> is set while it waits to load again after a failure.</summary>
public sealed record TranscriberState(TranscriberStatus Status, string? Runtime, bool OnGpu, Exception? Error, TimeSpan RetryIn);

/// <summary>A pass failed and the transcriber is being reloaded; the caller may retry the same audio.</summary>
public sealed class TranscriberFailedException(Exception inner)
    : Exception($"Transcriber failed: {inner.Message}", inner);

/// <summary>
/// Owns the transcriber: loads it, lends it to passes (several may run at once), and after a failed pass throws
/// it away and loads a fresh one, waiting <see cref="FirstRetry"/> doubling up to <see cref="MaxRetry"/>.
/// Callers of <see cref="TranscribeAsync"/> wait while it loads, so nothing is lost meanwhile. A discarded
/// transcriber is disposed only after its last running pass returns.
/// </summary>
public sealed class TranscriberHost : IDisposable
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<ITranscriber>> _load;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private TaskCompletionSource<Lease> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Lease? _current;
    private readonly TimeSpan _firstRetry;
    private readonly TimeSpan _maxRetry;
    private TimeSpan _nextRetry;
    private int _injectFailure;

    public TranscriberHost(Func<CancellationToken, Task<ITranscriber>> load, ILogger logger, TimeProvider time)
        : this(load, logger, time, FirstRetry, MaxRetry)
    {
    }

    internal TranscriberHost(
        Func<CancellationToken, Task<ITranscriber>> load, ILogger logger, TimeProvider time, TimeSpan firstRetry, TimeSpan maxRetry)
    {
        _firstRetry = firstRetry;
        _maxRetry = maxRetry;
        _nextRetry = firstRetry;
        _load = load;
        _logger = logger.ForContext<TranscriberHost>();
        _time = time;
    }

    public event EventHandler<TranscriberState>? StateChanged;

    public TranscriberState State { get; private set; } = new(TranscriberStatus.Loading, null, false, null, TimeSpan.Zero);

    public bool IsReady => State.Status == TranscriberStatus.Ready;

    public void Start() => _ = LoadAsync(TimeSpan.Zero);

    /// <summary>Recognizes with the current transcriber, waiting while it loads.</summary>
    /// <exception cref="TranscriberFailedException">The pass failed; a fresh transcriber is on its way.</exception>
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        float[] samples, string language, CancellationToken cancellation)
    {
        Lease lease = await AcquireAsync(cancellation);
        try
        {
            if (Interlocked.Exchange(ref _injectFailure, 0) == 1)
            {
                throw new InvalidOperationException("Simulated transcriber failure");
            }

            IReadOnlyList<TranscriptSegment> segments = await lease.Transcriber.TranscribeAsync(samples, language, cancellation);
            lock (_gate)
            {
                _nextRetry = _firstRetry;
            }

            return segments;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            Fail(lease, ex);
            throw new TranscriberFailedException(ex);
        }
        finally
        {
            Release(lease);
        }
    }

    /// <summary>Makes the next pass fail, to check recovery by hand.</summary>
    public void InjectFailure() => Interlocked.Exchange(ref _injectFailure, 1);

    public void Dispose()
    {
        _stop.Cancel();
        Lease? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
            if (current is not null)
            {
                current.Retired = true;
            }
        }

        if (current is not null)
        {
            Release(current, acquired: false);
        }
    }

    private async Task<Lease> AcquireAsync(CancellationToken cancellation)
    {
        while (true)
        {
            Task<Lease> ready;
            lock (_gate)
            {
                ready = _ready.Task;
            }

            Lease lease = await ready.WaitAsync(cancellation);
            lock (_gate)
            {
                if (!lease.Retired)
                {
                    lease.Active++;
                    return lease;
                }
            }
        }
    }

    private void Release(Lease lease, bool acquired = true)
    {
        bool dispose;
        lock (_gate)
        {
            if (acquired)
            {
                lease.Active--;
            }

            dispose = lease.Retired && lease.Active == 0 && !lease.Disposed;
            lease.Disposed |= dispose;
        }

        if (dispose)
        {
            lease.Transcriber.Dispose();
        }
    }

    private void Fail(Lease lease, Exception error)
    {
        TimeSpan retryIn;
        lock (_gate)
        {
            if (lease.Retired)
            {
                return; // another pass already reported this transcriber
            }

            lease.Retired = true;
            _current = null;
            _ready = new TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously);
            retryIn = NextRetry();
        }

        _logger.Error(error, "Transcriber failed; loading a fresh one in {RetryIn}", retryIn);
        SetState(new TranscriberState(TranscriberStatus.Failed, null, false, error, retryIn));
        _ = LoadAsync(retryIn);
    }

    private async Task LoadAsync(TimeSpan delay)
    {
        while (true)
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _time, _stop.Token);
                }

                SetState(new TranscriberState(TranscriberStatus.Loading, null, false, null, TimeSpan.Zero));
                ITranscriber transcriber = await _load(_stop.Token);
                var lease = new Lease(transcriber);
                TaskCompletionSource<Lease> ready;
                lock (_gate)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        lease.Retired = true;
                    }
                    else
                    {
                        _current = lease;
                    }

                    ready = _ready;
                }

                if (lease.Retired)
                {
                    Release(lease, acquired: false);
                    return;
                }

                SetState(new TranscriberState(TranscriberStatus.Ready, transcriber.Runtime, transcriber.OnGpu, null, TimeSpan.Zero));
                ready.TrySetResult(lease);
                return;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    delay = NextRetry();
                }

                _logger.Error(ex, "Loading the transcriber failed; next attempt in {RetryIn}", delay);
                SetState(new TranscriberState(TranscriberStatus.Failed, null, false, ex, delay));
            }
        }
    }

    private TimeSpan NextRetry()
    {
        TimeSpan retry = _nextRetry;
        _nextRetry = TimeSpan.FromTicks(Math.Min(_nextRetry.Ticks * 2, _maxRetry.Ticks));
        return retry;
    }

    private void SetState(TranscriberState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private sealed class Lease(ITranscriber transcriber)
    {
        public readonly ITranscriber Transcriber = transcriber;
        public int Active;
        public bool Retired;
        public bool Disposed;
    }
}

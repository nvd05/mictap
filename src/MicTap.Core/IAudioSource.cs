namespace MicTap.Core;

/// <summary>Captured PCM; <see cref="Buffer"/> is reused by the source once the handler returns.</summary>
public sealed record AudioBuffer(byte[] Buffer, int Count)
{
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Count);
}

/// <summary>Capture has ended; <see cref="Error"/> is set when a failure ended it.</summary>
public sealed record CaptureStopped(Exception? Error);

/// <summary>Live capture in <see cref="PcmFormat.Mono16k"/>. Events may arrive on any thread.</summary>
public interface IAudioSource : IDisposable
{
    event EventHandler<AudioBuffer>? DataAvailable;

    /// <summary>Raised once when capture has ended, after the last <see cref="DataAvailable"/>.</summary>
    event EventHandler<CaptureStopped>? Stopped;

    void Start();

    /// <summary>Asks capture to end; <see cref="Stopped"/> follows.</summary>
    void Stop();
}

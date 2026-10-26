namespace MicTap.Core.Tests;

/// <summary>An audio source driven by the test instead of a microphone.</summary>
internal sealed class FakeAudioSource : IAudioSource
{
    public event EventHandler<AudioBuffer>? DataAvailable;

    public event EventHandler<CaptureStopped>? Stopped;

    public bool Started { get; private set; }

    public bool StopRequested { get; private set; }

    public bool Disposed { get; private set; }

    public void Start() => Started = true;

    /// <summary>Like WaveInEvent: reports the end of capture after the last delivered block.</summary>
    public void Stop()
    {
        StopRequested = true;
        Stopped?.Invoke(this, new CaptureStopped(null));
    }

    /// <summary>Delivers <paramref name="pcm"/> inside a larger buffer, as NAudio does, so Count must be honored.</summary>
    public void Push(byte[] pcm)
    {
        var buffer = new byte[ pcm.Length + 64 ];
        pcm.CopyTo(buffer, 0);
        buffer.AsSpan(pcm.Length).Fill(0xEE);
        DataAvailable?.Invoke(this, new AudioBuffer(buffer, pcm.Length));
    }

    public void Fail(Exception error) => Stopped?.Invoke(this, new CaptureStopped(error));

    public void Dispose() => Disposed = true;
}

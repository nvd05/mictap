namespace MicTap.Core;

/// <summary>Uncompressed PCM format of recorded audio.</summary>
public sealed record PcmFormat(int SampleRate, int Channels, int BitsPerSample)
{
    /// <summary>The format mictap records: 16 kHz, mono, 16-bit.</summary>
    public static PcmFormat Mono16k { get; } = new(16000, 1, 16);

    public int BlockAlign => Channels * BitsPerSample / 8;

    public int ByteRate => SampleRate * BlockAlign;

    public TimeSpan DurationOf(long bytes) => TimeSpan.FromTicks(bytes * TimeSpan.TicksPerSecond / ByteRate);
}

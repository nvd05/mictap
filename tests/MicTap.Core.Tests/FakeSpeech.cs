namespace MicTap.Core.Tests;

/// <summary>Audio where speech is simply signal and silence is zeros, and a VAD stand-in that sees exactly that.</summary>
internal static class FakeSpeech
{
    public const int Rate = 16000;

    /// <summary>Speech is any 10 ms frame with signal; gaps under 500 ms are bridged, as the real VAD does.</summary>
    public static IReadOnlyList<SpeechSpan> Detect(float[] samples)
    {
        const int frame = Rate / 100;
        var spans = new List<(int Start, int End)>();
        for (int at = 0; at + frame <= samples.Length; at += frame)
        {
            if (samples.AsSpan(at, frame).IndexOfAnyExcept(0f) < 0)
            {
                continue;
            }

            if (spans.Count > 0 && at - spans[ ^1 ].End < Rate / 2)
            {
                spans[ ^1 ] = (spans[ ^1 ].Start, at + frame);
            }
            else
            {
                spans.Add((at, at + frame));
            }
        }

        return [ .. spans.Select(s => new SpeechSpan(Time(s.Start), Time(s.End))) ];
    }

    public static float[] Speech(double seconds) =>
        [ .. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (float)Math.Sin(i * 0.05) * 0.3f + 0.01f) ];

    public static float[] Silence(double seconds) => new float[ (int)(seconds * Rate) ];

    public static byte[] SpeechPcm(double seconds) => SyntheticPcm.Create(TimeSpan.FromSeconds(seconds));

    public static byte[] SilencePcm(double seconds) => new byte[ (int)(seconds * Rate) * 2 ];

    private static TimeSpan Time(int samples) => TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / Rate);
}

/// <summary>A transcriber driven by the test; text comes from <paramref name="recognize"/>.</summary>
internal sealed class FakeTranscriber(Func<float[], string, CancellationToken, Task<string>> recognize) : ITranscriber
{
    private int _calls;

    public string Runtime => "fake";

    public bool OnGpu => false;

    public int Calls => Volatile.Read(ref _calls);

    public bool Disposed { get; private set; }

    public static FakeTranscriber Saying(Func<float[], string, string> text) =>
        new((samples, language, _) => Task.FromResult(text(samples, language)));

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        float[] samples, string language, CancellationToken cancellation)
    {
        Interlocked.Increment(ref _calls);
        return [ new TranscriptSegment(TimeSpan.Zero, TimeSpan.Zero, await recognize(samples, language, cancellation)) ];
    }

    public void Dispose() => Disposed = true;
}

namespace MicTap.Core.Tests;

public sealed class SpeechSegmenterTests
{
    private const int Rate = 16000;

    [Fact]
    public void Silence_never_becomes_a_segment()
    {
        var segmenter = new SpeechSegmenter(LoudDetector, "ru");

        IReadOnlyList<SpeechSegment> segments = [.. FeedInBlocks(segmenter, Silence(40)), .. segmenter.Flush()];

        Assert.Empty(segments);
    }

    [Fact]
    public void Phrases_split_by_a_second_of_silence_become_segments_with_their_bounds()
    {
        var segmenter = new SpeechSegmenter(LoudDetector, "ru");
        float[] audio = [.. Silence(0.5), .. Speech(2), .. Silence(1), .. Speech(1.5), .. Silence(1)];

        IReadOnlyList<SpeechSegment> segments = [.. FeedInBlocks(segmenter, audio), .. segmenter.Flush()];

        Assert.Equal([(0.5, 2.5), (3.5, 5.0)], segments.Select(Bounds));
        Assert.All(segments, s => Assert.Equal(s.Samples.Length, (int)Math.Round((s.End - s.Start).TotalSeconds * Rate)));
    }

    [Fact]
    public void Segment_closes_only_after_half_a_second_of_silence()
    {
        var segmenter = new SpeechSegmenter(LoudDetector, "ru");

        Assert.Empty(FeedInBlocks(segmenter, [.. Speech(2), .. Silence(0.3)]));
        IReadOnlyList<SpeechSegment> closed = FeedInBlocks(segmenter, Silence(0.7));

        Assert.Equal([(0.0, 2.0)], closed.Select(Bounds));
    }

    [Fact]
    public void Speech_longer_than_thirty_seconds_is_cut_at_thirty()
    {
        var segmenter = new SpeechSegmenter(LoudDetector, "ru");

        IReadOnlyList<SpeechSegment> whileSpeaking = FeedInBlocks(segmenter, Speech(40));
        IReadOnlyList<SpeechSegment> rest = segmenter.Flush();

        Assert.Equal([(0.0, 30.0)], whileSpeaking.Select(Bounds));
        Assert.Equal([(30.0, 40.0)], rest.Select(Bounds));
    }

    [Fact]
    public void Language_mark_cuts_speech_and_switches_the_language()
    {
        var segmenter = new SpeechSegmenter(LoudDetector, "ru");
        FeedInBlocks(segmenter, Speech(3));

        IReadOnlyList<SpeechSegment> cut = segmenter.Mark(TimeSpan.FromSeconds(3), "ja");
        IReadOnlyList<SpeechSegment> after = [.. FeedInBlocks(segmenter, [.. Speech(2), .. Silence(1)]), .. segmenter.Flush()];

        Assert.Equal([(0.0, 3.0, "ru")], cut.Select(BoundsAndLanguage));
        Assert.Equal([(3.0, 5.0, "ja")], after.Select(BoundsAndLanguage));
    }

    [Fact]
    public void Mark_ahead_of_the_audio_applies_when_the_audio_reaches_it()
    {
        var segmenter = new SpeechSegmenter(LoudDetector, "ru");
        segmenter.Mark(TimeSpan.FromSeconds(2), "ja");

        IReadOnlyList<SpeechSegment> segments = [.. FeedInBlocks(segmenter, [.. Speech(4), .. Silence(1)]), .. segmenter.Flush()];

        Assert.Equal([(0.0, 2.0, "ru"), (2.0, 4.0, "ja")], segments.Select(BoundsAndLanguage));
    }

    [Fact]
    public void Flush_ends_speech_that_is_still_going_on()
    {
        var segmenter = new SpeechSegmenter(LoudDetector, "ja");
        FeedInBlocks(segmenter, [.. Silence(2), .. Speech(1.2)]);

        Assert.Equal([(2.0, 3.2, "ja")], segmenter.Flush().Select(BoundsAndLanguage));
    }

    /// <summary>Feeds 100 ms blocks, as the microphone delivers them.</summary>
    private static List<SpeechSegment> FeedInBlocks(SpeechSegmenter segmenter, float[] audio)
    {
        var segments = new List<SpeechSegment>();
        for (int at = 0; at < audio.Length; at += Rate / 10)
        {
            segments.AddRange(segmenter.Add(audio.AsSpan(at, Math.Min(Rate / 10, audio.Length - at))));
        }

        return segments;
    }

    /// <summary>A stand-in for the VAD: speech is any 10 ms frame with signal; gaps under 500 ms are bridged.</summary>
    private static IReadOnlyList<SpeechSpan> LoudDetector(float[] samples)
    {
        const int frame = Rate / 100;
        var spans = new List<(int Start, int End)>();
        for (int at = 0; at + frame <= samples.Length; at += frame)
        {
            if (samples.AsSpan(at, frame).IndexOfAnyExcept(0f) < 0)
            {
                continue;
            }

            if (spans.Count > 0 && at - spans[^1].End < Rate / 2)
            {
                spans[^1] = (spans[^1].Start, at + frame);
            }
            else
            {
                spans.Add((at, at + frame));
            }
        }

        return [.. spans.Select(s => new SpeechSpan(Time(s.Start), Time(s.End)))];
    }

    private static TimeSpan Time(int samples) => TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / Rate);

    private static float[] Speech(double seconds) =>
        [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (float)Math.Sin(i * 0.05) * 0.3f + 0.01f)];

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static (double, double) Bounds(SpeechSegment s) => (Math.Round(s.Start.TotalSeconds, 2), Math.Round(s.End.TotalSeconds, 2));

    private static (double, double, string) BoundsAndLanguage(SpeechSegment s) =>
        (Math.Round(s.Start.TotalSeconds, 2), Math.Round(s.End.TotalSeconds, 2), s.Language);
}

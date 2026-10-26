namespace MicTap.Core.Tests;

public sealed class SpeechSegmenterTests
{
    private const int Rate = 16000;

    [Fact]
    public void Silence_never_becomes_a_segment()
    {
        var segmenter = new SpeechSegmenter(FakeSpeech.Detect, "ru");

        IReadOnlyList<SpeechSegment> segments = [.. FeedInBlocks(segmenter, FakeSpeech.Silence(40)), .. segmenter.Flush()];

        Assert.Empty(segments);
    }

    [Fact]
    public void Phrases_split_by_a_second_of_silence_become_segments_with_their_bounds()
    {
        var segmenter = new SpeechSegmenter(FakeSpeech.Detect, "ru");
        float[] audio = [.. FakeSpeech.Silence(0.5), .. FakeSpeech.Speech(2), .. FakeSpeech.Silence(1), .. FakeSpeech.Speech(1.5), .. FakeSpeech.Silence(1)];

        IReadOnlyList<SpeechSegment> segments = [.. FeedInBlocks(segmenter, audio), .. segmenter.Flush()];

        Assert.Equal([(0.5, 2.5), (3.5, 5.0)], segments.Select(Bounds));
        Assert.All(segments, s => Assert.Equal(s.Samples.Length, (int)Math.Round((s.End - s.Start).TotalSeconds * Rate)));
    }

    [Fact]
    public void Segment_closes_only_after_half_a_second_of_silence()
    {
        var segmenter = new SpeechSegmenter(FakeSpeech.Detect, "ru");

        Assert.Empty(FeedInBlocks(segmenter, [.. FakeSpeech.Speech(2), .. FakeSpeech.Silence(0.3)]));
        IReadOnlyList<SpeechSegment> closed = FeedInBlocks(segmenter, FakeSpeech.Silence(0.7));

        Assert.Equal([(0.0, 2.0)], closed.Select(Bounds));
    }

    [Fact]
    public void Speech_longer_than_thirty_seconds_is_cut_at_thirty()
    {
        var segmenter = new SpeechSegmenter(FakeSpeech.Detect, "ru");

        IReadOnlyList<SpeechSegment> whileSpeaking = FeedInBlocks(segmenter, FakeSpeech.Speech(40));
        IReadOnlyList<SpeechSegment> rest = segmenter.Flush();

        Assert.Equal([(0.0, 30.0)], whileSpeaking.Select(Bounds));
        Assert.Equal([(30.0, 40.0)], rest.Select(Bounds));
    }

    [Fact]
    public void Language_mark_cuts_speech_and_switches_the_language()
    {
        var segmenter = new SpeechSegmenter(FakeSpeech.Detect, "ru");
        FeedInBlocks(segmenter, FakeSpeech.Speech(3));

        IReadOnlyList<SpeechSegment> cut = segmenter.Mark(TimeSpan.FromSeconds(3), "ja");
        IReadOnlyList<SpeechSegment> after = [.. FeedInBlocks(segmenter, [.. FakeSpeech.Speech(2), .. FakeSpeech.Silence(1)]), .. segmenter.Flush()];

        Assert.Equal([(0.0, 3.0, "ru")], cut.Select(BoundsAndLanguage));
        Assert.Equal([(3.0, 5.0, "ja")], after.Select(BoundsAndLanguage));
    }

    [Fact]
    public void Mark_ahead_of_the_audio_applies_when_the_audio_reaches_it()
    {
        var segmenter = new SpeechSegmenter(FakeSpeech.Detect, "ru");
        segmenter.Mark(TimeSpan.FromSeconds(2), "ja");

        IReadOnlyList<SpeechSegment> segments = [.. FeedInBlocks(segmenter, [.. FakeSpeech.Speech(4), .. FakeSpeech.Silence(1)]), .. segmenter.Flush()];

        Assert.Equal([(0.0, 2.0, "ru"), (2.0, 4.0, "ja")], segments.Select(BoundsAndLanguage));
    }

    [Fact]
    public void Flush_ends_speech_that_is_still_going_on()
    {
        var segmenter = new SpeechSegmenter(FakeSpeech.Detect, "ja");
        FeedInBlocks(segmenter, [.. FakeSpeech.Silence(2), .. FakeSpeech.Speech(1.2)]);

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

    private static (double, double) Bounds(SpeechSegment s) => (Math.Round(s.Start.TotalSeconds, 2), Math.Round(s.End.TotalSeconds, 2));

    private static (double, double, string) BoundsAndLanguage(SpeechSegment s) =>
        (Math.Round(s.Start.TotalSeconds, 2), Math.Round(s.End.TotalSeconds, 2), s.Language);
}

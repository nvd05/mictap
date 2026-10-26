using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Xunit.Abstractions;

namespace MicTap.Core.Tests;

/// <summary>Runs only where the VAD model is downloaded (the app does it on first start) and a Russian voice is installed.</summary>
public sealed class VadFactAttribute : FactAttribute
{
    public VadFactAttribute()
    {
        if (!VadFixture.Store.IsReady)
        {
            Skip = $"No VAD model at {VadFixture.Store.ModelPath}";
        }
        else if (WhisperFixture.RussianVoice() is null)
        {
            Skip = "No ru-RU voice for System.Speech";
        }
    }
}

/// <summary>Three synthesized Russian phrases, each trimmed to its audible part, and the Silero model.</summary>
public sealed class VadFixture : IDisposable
{
    public static ModelStore Store { get; } = new(AppPaths.ModelsFolder, ModelSpec.SileroVad);

    public VadFixture()
    {
        if (!Store.IsReady || WhisperFixture.RussianVoice() is not { } voice)
        {
            return;
        }

        using var synth = new SpeechSynthesizer();
        synth.SelectVoice(voice.Name);
        Phrases = [.. new[] { "Добрый вечер", "Сегодня мы проверяем деление речи на фразы", "Спасибо за внимание" }
            .Select(text => Audible(Speak(synth, text)))];
        Model = VoiceActivityModel.Load(Store.ModelPath);
    }

    public float[][] Phrases { get; } = [];

    public VoiceActivityModel? Model { get; }

    public void Dispose() => Model?.Dispose();

    private static float[] Speak(SpeechSynthesizer synth, string text)
    {
        using var pcm = new MemoryStream();
        synth.SetOutputToAudioStream(pcm, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak(text);
        synth.SetOutputToNull();
        var ring = new AudioRing((int)pcm.Length / 2);
        ring.Write(pcm.ToArray());
        return ring.Snapshot();
    }

    private static float[] Audible(float[] samples)
    {
        int first = Array.FindIndex(samples, s => Math.Abs(s) > 0.005f);
        int last = Array.FindLastIndex(samples, s => Math.Abs(s) > 0.005f);
        return samples[first..(last + 1)];
    }
}

public sealed class SileroSegmentationTests(VadFixture fixture, ITestOutputHelper output) : IClassFixture<VadFixture>
{
    private const int Rate = 16000;
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(100);

    [VadFact]
    public void Phrases_with_a_second_of_silence_between_them_become_one_segment_each()
    {
        var audio = new List<float>(new float[Rate]);
        var expected = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (float[] phrase in fixture.Phrases)
        {
            expected.Add((Time(audio.Count), Time(audio.Count + phrase.Length)));
            audio.AddRange(phrase);
            audio.AddRange(new float[Rate]);
        }

        var segmenter = new SpeechSegmenter(fixture.Model!.CreateDetector(), "ru");
        var segments = new List<SpeechSegment>();
        float[] samples = [.. audio];
        for (int at = 0; at < samples.Length; at += Rate / 10)
        {
            segments.AddRange(segmenter.Add(samples.AsSpan(at, Math.Min(Rate / 10, samples.Length - at))));
        }

        segments.AddRange(segmenter.Flush());

        foreach (SpeechSegment s in segments)
        {
            output.WriteLine($"{s.Start.TotalSeconds:0.000} - {s.End.TotalSeconds:0.000}");
        }

        Assert.Equal(expected.Count, segments.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.InRange(segments[i].Start, expected[i].Start - Tolerance, expected[i].Start + Tolerance);
            Assert.InRange(segments[i].End, expected[i].End - Tolerance, expected[i].End + Tolerance);
        }
    }

    [VadFact]
    public void Silence_gives_no_segments()
    {
        var segmenter = new SpeechSegmenter(fixture.Model!.CreateDetector(), "ru");
        var random = new Random(1);
        float[] roomNoise = [.. Enumerable.Range(0, Rate * 30).Select(_ => (float)(random.NextDouble() - 0.5) * 0.002f)];

        var segments = new List<SpeechSegment>();
        for (int at = 0; at < roomNoise.Length; at += Rate / 10)
        {
            segments.AddRange(segmenter.Add(roomNoise.AsSpan(at, Rate / 10)));
        }

        segments.AddRange(segmenter.Flush());

        Assert.Empty(segments);
    }

    private static TimeSpan Time(int samples) => TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / Rate);
}

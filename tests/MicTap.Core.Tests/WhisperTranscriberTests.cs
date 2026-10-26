using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Serilog;
using Xunit.Abstractions;

namespace MicTap.Core.Tests;

/// <summary>Runs only where the model is downloaded (the app does it on first start) and a Russian voice is installed.</summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        if (!WhisperFixture.Store.IsReady)
        {
            Skip = $"No model at {WhisperFixture.Store.ModelPath}";
        }
        else if (WhisperFixture.RussianVoice() is null)
        {
            Skip = "No ru-RU voice for System.Speech";
        }
    }
}

/// <summary>Loads the model once and synthesizes the Russian phrase into a 16 kHz mono WAV fixture.</summary>
public sealed class WhisperFixture : IDisposable
{
    public const string Phrase = "Сегодня на лекции мы изучаем распознавание речи.";

    private readonly TempFolder _temp = new();

    public static ModelStore Store { get; } = new(AppPaths.ModelsFolder, ModelSpec.LargeV3Turbo);

    public WhisperFixture()
    {
        if (!Store.IsReady || RussianVoice() is not { } voice)
        {
            return;
        }

        WavPath = Path.Combine(_temp.Path, "phrase.wav");
        using (var synth = new SpeechSynthesizer())
        {
            synth.SelectVoice(voice.Name);
            synth.SetOutputToWaveFile(
                WavPath, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(Phrase);
        }

        Transcriber = WhisperTranscriber.Load(Store.ModelPath, new LoggerConfiguration().CreateLogger());
    }

    public string? WavPath { get; }

    public WhisperTranscriber? Transcriber { get; }

    public static VoiceInfo? RussianVoice()
    {
        using var synth = new SpeechSynthesizer();
        return synth.GetInstalledVoices(new CultureInfo("ru-RU")).FirstOrDefault(v => v.Enabled)?.VoiceInfo;
    }

    public void Dispose()
    {
        Transcriber?.Dispose();
        _temp.Dispose();
    }
}

public sealed class WhisperTranscriberTests(WhisperFixture fixture, ITestOutputHelper output) : IClassFixture<WhisperFixture>
{
    [ModelFact]
    public async Task Recognizes_the_synthesized_russian_phrase()
    {
        float[] samples = WavSamples.Read(fixture.WavPath!);

        IReadOnlyList<TranscriptSegment> segments =
            await fixture.Transcriber!.TranscribeAsync(samples, "ru", CancellationToken.None);

        string text = string.Join(" ", segments.Select(s => s.Text)).ToLowerInvariant();
        output.WriteLine($"{fixture.Transcriber.Runtime}: {text}");
        Assert.Contains("сегодня", text);
        Assert.Contains("лекци", text);
        Assert.Contains("распозна", text);
    }

    [ModelFact]
    public void Reports_the_runtime_that_was_loaded()
    {
        Assert.Contains(fixture.Transcriber!.Runtime, new[] { "CUDA", "CPU" });
    }
}

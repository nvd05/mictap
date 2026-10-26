using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace MicTap.App;

/// <summary>A short tone on a language switch — higher for Japanese than for Russian — so it is heard without looking.</summary>
public static class LanguageCue
{
    private static readonly TimeSpan Length = TimeSpan.FromMilliseconds(150);

    public static void Play(string language, ILogger logger)
    {
        double frequency = language switch
        {
            "ru" => 660,
            "ja" => 1046,
            _ => 830,
        };
        ISampleProvider tone = new SignalGenerator(44100, 1) { Type = SignalGeneratorType.Sin, Frequency = frequency, Gain = 0.2 }
            .Take(Length);
        var output = new WaveOutEvent();
        try
        {
            output.Init(tone);
        }
        catch (NAudio.MmException ex)
        {
            output.Dispose();
            logger.Warning(ex, "No sound device for the language cue");
            return;
        }

        output.PlaybackStopped += (_, _) => output.Dispose();
        output.Play();
    }
}

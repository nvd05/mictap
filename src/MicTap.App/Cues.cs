using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace MicTap.App;

/// <summary>Short tones for what a hotkey did, so it is heard without looking.</summary>
public static class Cues
{
    /// <summary>A language switch: higher for Japanese than for Russian.</summary>
    public static void Language(string language, ILogger logger) =>
        Play(
            logger,
            (language switch
            {
                "ru" => 660,
                "ja" => 1046,
                _ => 830,
            }, 150));

    /// <summary>The text went to the agents: two quick rising tones, unlike any language.</summary>
    public static void Pushed(ILogger logger) => Play(logger, (784, 80), (1175, 80));

    private static void Play(ILogger logger, params (double Frequency, int Milliseconds)[] notes)
    {
        ISampleProvider tone = notes
            .Select(note => new SignalGenerator(44100, 1) { Type = SignalGeneratorType.Sin, Frequency = note.Frequency, Gain = 0.2 }
                .Take(TimeSpan.FromMilliseconds(note.Milliseconds)))
            .Aggregate((first, next) => first.FollowedBy(next));
        var output = new WaveOutEvent();
        try
        {
            output.Init(tone);
        }
        catch (NAudio.MmException ex)
        {
            output.Dispose();
            logger.Warning(ex, "No sound device for the cue");
            return;
        }

        output.PlaybackStopped += (_, _) => output.Dispose();
        output.Play();
    }
}

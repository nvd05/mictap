using Whisper.net;

namespace MicTap.Core;

/// <summary>A stretch of speech, relative to the start of the analysed samples.</summary>
public sealed record SpeechSpan(TimeSpan Start, TimeSpan End);

/// <summary>Finds speech in 16 kHz mono samples. Not thread-safe: one detector per thread.</summary>
public delegate IReadOnlyList<SpeechSpan> SpeechDetector(float[] samples);

/// <summary>
/// Whisper.net's Silero VAD on the CPU. The model is loaded once; each thread that detects speech takes its own
/// detector from <see cref="CreateDetector"/>.
/// </summary>
public sealed class VoiceActivityModel : IDisposable
{
    private static readonly TimeSpan MinSpeech = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Padding = TimeSpan.FromMilliseconds(30); // whisper.cpp default

    private readonly WhisperVadFactory _factory;
    private readonly List<WhisperVadProcessor> _processors = [];
    private readonly Lock _gate = new();

    private VoiceActivityModel(WhisperVadFactory factory) => _factory = factory;

    public static VoiceActivityModel Load(string modelPath) =>
        new(WhisperVadFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false }));

    /// <summary>A detector for one thread: 16 kHz mono samples in, spans of speech out.</summary>
    public SpeechDetector CreateDetector()
    {
        WhisperVadProcessor processor = _factory.CreateBuilder()
            .WithUseGpu(false)
            .WithThreads(2)
            .WithMinSpeechDuration(MinSpeech)
            .WithMinSilenceDuration(SpeechSegmenter.MinSilence)
            .WithMaxSpeechDuration(SpeechSegmenter.MaxSegment)
            .WithSpeechPadding(Padding)
            .Build();
        lock (_gate)
        {
            _processors.Add(processor);
        }

        return samples => [.. processor.DetectSpeech(samples).Select(s => new SpeechSpan(s.Start, s.End))];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (WhisperVadProcessor processor in _processors)
            {
                processor.Dispose();
            }
        }

        _factory.Dispose();
    }
}

using System.Collections.Concurrent;
using Serilog;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace MicTap.Core;

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Speech recognition engine. Thread-safe: callers may transcribe concurrently.</summary>
public interface ITranscriber : IDisposable
{
    /// <summary>The runtime that actually runs recognition, as shown to the user, e.g. "CUDA" or "CPU".</summary>
    string Runtime { get; }

    /// <summary>True when recognition runs on the GPU.</summary>
    bool OnGpu { get; }

    /// <summary>Recognizes 16 kHz mono samples, -1..1.</summary>
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(float[] samples, string language, CancellationToken cancellation);
}

/// <summary>
/// Whisper.net with the ggml model loaded once. Each concurrent call gets its own processor (whisper state); the
/// model weights are shared.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    private readonly WhisperFactory _factory;
    private readonly ConcurrentDictionary<string, ConcurrentBag<WhisperProcessor>> _idle = new();
    private readonly int _threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    private WhisperTranscriber(WhisperFactory factory, RuntimeLibrary? runtime)
    {
        _factory = factory;
        OnGpu = runtime is RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 or RuntimeLibrary.Vulkan;
        Runtime = runtime switch
        {
            RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => "CUDA",
            RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => "CPU",
            null => "не загружен",
            _ => runtime.Value.ToString(),
        };
    }

    public string Runtime { get; }

    public bool OnGpu { get; }

    /// <summary>Loads the model; Whisper.net picks CUDA when its native libraries load, otherwise the CPU.</summary>
    public static WhisperTranscriber Load(string modelPath, ILogger logger)
    {
        WhisperFactory factory = WhisperFactory.FromPath(modelPath);
        var transcriber = new WhisperTranscriber(factory, RuntimeOptions.LoadedLibrary);
        logger.ForContext<WhisperTranscriber>().Information(
            "Whisper model {Model} loaded, runtime {Runtime}: {Info}",
            Path.GetFileName(modelPath), RuntimeOptions.LoadedLibrary, WhisperFactory.GetRuntimeInfo());
        return transcriber;
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        float[] samples, string language, CancellationToken cancellation)
    {
        ConcurrentBag<WhisperProcessor> idle = _idle.GetOrAdd(language, _ => [ ]);
        WhisperProcessor processor = idle.TryTake(out WhisperProcessor? free)
            ? free
            : _factory.CreateBuilder().WithLanguage(language).WithThreads(_threads).Build();
        try
        {
            var segments = new List<TranscriptSegment>();
            await foreach (SegmentData segment in processor.ProcessAsync(samples, cancellation))
            {
                segments.Add(new TranscriptSegment(segment.Start, segment.End, segment.Text.Trim()));
            }

            return segments;
        }
        finally
        {
            idle.Add(processor);
        }
    }

    public void Dispose()
    {
        foreach (WhisperProcessor processor in _idle.Values.SelectMany(bag => bag))
        {
            processor.Dispose();
        }

        _factory.Dispose();
    }
}

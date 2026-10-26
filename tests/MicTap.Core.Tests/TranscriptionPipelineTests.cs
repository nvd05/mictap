using System.Diagnostics;
using System.Text.Json;
using Serilog.Core;

namespace MicTap.Core.Tests;

public sealed class TranscriptionPipelineTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempFolder _temp = new();
    private readonly FakeAudioSource _source = new();
    private readonly Recorder _recorder = new(Logger.None, new ManualTimeProvider(new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.FromHours(3))));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Every_closed_chunk_becomes_a_line_in_transcript_jsonl()
    {
        using TranscriptionPipeline pipeline = Run(new FakeTranscriber((samples, _) => Task.FromResult($"{samples.Length} samples")));
        var transcribed = new List<ChunkTranscript>();
        pipeline.ChunkTranscribed += (_, chunk) => { lock (transcribed) transcribed.Add(chunk); };
        SessionInfo session = _recorder.Start(Options(chunkSeconds: 1), _source);

        foreach (byte[] block in SyntheticPcm.Split(SyntheticPcm.Create(TimeSpan.FromSeconds(2.5)), 3200))
        {
            _source.Push(block);
        }

        await _recorder.StopAsync();
        await Eventually(() => { lock (transcribed) return transcribed.Count == 3; });

        string[] lines = File.ReadAllLines(Path.Combine(session.Folder, "transcript.jsonl"));
        Assert.Equal(
            [
                """{"schemaVersion":1,"chunk":0,"start":0,"end":1,"text":"16000 samples"}""",
                """{"schemaVersion":1,"chunk":1,"start":1,"end":2,"text":"16000 samples"}""",
                """{"schemaVersion":1,"chunk":2,"start":2,"end":2.5,"text":"8000 samples"}""",
            ],
            lines);
    }

    [Fact]
    public async Task Live_text_covers_at_most_the_last_ten_seconds()
    {
        using TranscriptionPipeline pipeline = Run(new FakeTranscriber((samples, _) => Task.FromResult($"{samples.Length}")));
        var live = new List<string>();
        pipeline.LiveTextChanged += (_, text) => { lock (live) live.Add(text.Text); };
        _recorder.Start(Options(chunkSeconds: 60), _source);

        foreach (byte[] block in SyntheticPcm.Split(SyntheticPcm.Create(TimeSpan.FromSeconds(12)), 3200))
        {
            _source.Push(block);
        }

        await Eventually(() => { lock (live) return live.Contains("160000"); });
        await _recorder.StopAsync();
    }

    [Fact]
    public async Task Failed_transcriber_is_reported_once_and_recording_goes_on()
    {
        using TranscriptionPipeline pipeline = Run(new FakeTranscriber((_, _) => throw new InvalidOperationException("CUDA out of memory")));
        var failures = new List<Exception>();
        pipeline.Failed += (_, error) => { lock (failures) failures.Add(error); };
        SessionInfo session = _recorder.Start(Options(chunkSeconds: 1), _source);
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(3));
        byte[][] blocks = SyntheticPcm.Split(pcm, 3200).ToArray();

        foreach (byte[] block in blocks[..10])
        {
            _source.Push(block);
        }

        await Eventually(() => { lock (failures) return failures.Count == 1; });
        foreach (byte[] block in blocks[10..])
        {
            _source.Push(block);
        }

        Assert.True(_recorder.IsRecording);
        await _recorder.StopAsync();
        Assert.Equal("CUDA out of memory", Assert.Single(failures).Message);
        Assert.Equal(pcm, WavFiles.ReadAudio(WavFiles.Chunks(session.AudioFolder)));
    }

    [Fact]
    public async Task Stuck_transcriber_neither_blocks_capture_nor_loses_audio()
    {
        using TranscriptionPipeline pipeline = Run(new FakeTranscriber((_, cancel) => Task.Delay(Timeout.Infinite, cancel).ContinueWith(_ => "", TaskScheduler.Default)));
        SessionInfo session = _recorder.Start(Options(chunkSeconds: 1), _source);
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(3));

        var capture = Stopwatch.StartNew();
        foreach (byte[] block in SyntheticPcm.Split(pcm, 3200))
        {
            _source.Push(block);
            Thread.Sleep(1); // let live passes start and hang
        }

        capture.Stop();
        Assert.True(capture.Elapsed < TimeSpan.FromSeconds(2), $"capture blocked for {capture.Elapsed}");
        await _recorder.StopAsync();
        Assert.Equal(pcm, WavFiles.ReadAudio(WavFiles.Chunks(session.AudioFolder)));
    }

    [Fact]
    public async Task Edit_through_the_sink_rewrites_the_line()
    {
        using TranscriptionPipeline pipeline = Run(new FakeTranscriber((_, _) => Task.FromResult("черновик")));
        var transcribed = new List<ChunkTranscript>();
        pipeline.ChunkTranscribed += (_, chunk) => { lock (transcribed) transcribed.Add(chunk); };
        SessionInfo session = _recorder.Start(Options(chunkSeconds: 60), _source);
        _source.Push(SyntheticPcm.Create(TimeSpan.FromSeconds(1)));
        await _recorder.StopAsync();
        await Eventually(() => { lock (transcribed) return transcribed.Count == 1; });

        pipeline.SinkFor(session).Put(transcribed[0].Line with { Text = "исправлено" });

        string line = Assert.Single(File.ReadAllLines(Path.Combine(session.Folder, "transcript.jsonl")));
        Assert.Equal("исправлено", JsonDocument.Parse(line).RootElement.GetProperty("text").GetString());
    }

    private TranscriptionPipeline Run(FakeTranscriber transcriber)
    {
        var pipeline = new TranscriptionPipeline(transcriber, () => "ru", Logger.None, TimeSpan.FromMilliseconds(50));
        pipeline.Attach(_recorder);
        pipeline.Start();
        return pipeline;
    }

    private RecordingOptions Options(int chunkSeconds) => new(_temp.Path, "Fake microphone", chunkSeconds, "0.2.0-test");

    private static async Task Eventually(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, "condition not met in time");
            await Task.Delay(20);
        }
    }

    private sealed class FakeTranscriber(Func<float[], CancellationToken, Task<string>> recognize) : ITranscriber
    {
        public string Runtime => "fake";

        public bool OnGpu => false;

        public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            float[] samples, string language, CancellationToken cancellation) =>
            [new TranscriptSegment(TimeSpan.Zero, TimeSpan.Zero, await recognize(samples, cancellation))];

        public void Dispose()
        {
        }
    }
}

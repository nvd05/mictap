using System.Text.Json;
using Serilog.Core;

namespace MicTap.Core.Tests;

public sealed class SessionComparisonTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 23, 0, 0, TimeSpan.FromHours(5));

    private readonly TempFolder _temp = new();
    private readonly SessionInfo _session;

    public SessionComparisonTests()
    {
        _session = new SessionInfo("Пара", _temp.Path);
        Directory.CreateDirectory(_session.AudioFolder);
        using (var writer = new ChunkedWavWriter(_session.AudioFolder, PcmFormat.Mono16k, 60))
        {
            writer.Write([ .. FakeSpeech.SpeechPcm(1), .. FakeSpeech.SilencePcm(1), .. FakeSpeech.SpeechPcm(1.5), .. FakeSpeech.SilencePcm(1) ]);
        }

        new SessionMeta(
            [ new SessionStart(Now, 0) ], "Mic", MetaFormat.From(PcmFormat.Mono16k), 60, "test", [ new LanguageMark(0, "ru") ]).WriteTo(_session);
        File.WriteAllText(Path.Combine(_temp.Path, "transcript.jsonl"), "свой текст\n");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Every_engine_gets_the_same_phrases_and_the_comparison_is_written_beside_the_transcript()
    {
        int attempts = 0;
        var flaky = new FakeTranscriber((samples, _, _) => Interlocked.Increment(ref attempts) == 1
            ? throw new CloudTranscriptionException("Router AI ответил 502")
            : Task.FromResult($"b:{samples.Length / 16}"));
        var dead = new FakeTranscriber((_, _, _) => throw new CloudTranscriptionException("Router AI ответил 503: No available providers"));
        ComparedEngine[] engines =
        [
            new("Видеокарта · Whisper", FakeTranscriber.Saying((samples, language) => $"{language}:{samples.Length / 16}")),
            new("x-ai/grok-stt-1.0", flaky, () => 0.25),
            new("google/chirp-3", dead),
        ];

        ComparisonResult result = await new SessionComparison(() => FakeSpeech.Detect, new ManualTimeProvider(Now), Logger.None)
            .RunAsync(_session, "ru", engines, new Progress<double>(), CancellationToken.None);

        Assert.Equal(Path.Combine(_temp.Path, "compare", "2026-10-27_23-00-00"), result.Folder);
        Assert.Equal("свой текст\n", File.ReadAllText(Path.Combine(_temp.Path, "transcript.jsonl")));
        Assert.Equal(
            [ ("Видеокарта · Whisper", 2, 0, 0.0), ("x-ai/grok-stt-1.0", 2, 0, 0.25), ("google/chirp-3", 0, 2, 0.0) ],
            result.Engines.Select(e => (e.Engine, e.Phrases, e.Errors, e.Rubles)));

        string[] grok = File.ReadAllLines(Path.Combine(result.Folder, "x-ai__grok-stt-1.0.jsonl"));
        Assert.Equal([ "b:1000", "b:1500" ], grok.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("text").GetString()));
        string markdown = File.ReadAllText(Path.Combine(result.Folder, "comparison.md"));
        Assert.Contains("### 00:00:02–00:00:03 (ru)", markdown);
        Assert.Contains("- **Видеокарта · Whisper**: ru:1500", markdown);
        Assert.Contains("[ошибка: Router AI ответил 503: No available providers]", markdown);
        Assert.True(File.Exists(Path.Combine(result.Folder, "summary.json")));
    }
}

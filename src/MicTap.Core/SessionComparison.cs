using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;

namespace MicTap.Core;

/// <summary>An engine in a comparison: its name for the files, the transcriber, and what it has cost so far in rubles.</summary>
public sealed record ComparedEngine(string Name, ITranscriber Transcriber, Func<double>? Rubles = null);

/// <summary>How one engine did: phrases with text, phrases it failed on, rubles and the time it took altogether.</summary>
public sealed record ComparisonSummary(string Engine, int Phrases, int Errors, double Rubles, double Seconds);

public sealed record ComparisonResult(string Folder, IReadOnlyList<ComparisonSummary> Engines);

/// <summary>The local Whisper of the host as an engine: a failed transcriber is reloaded and the phrase goes again.</summary>
public sealed class HostTranscriber(TranscriberHost host) : ITranscriber
{
    public string Runtime => host.State.Runtime ?? "Whisper";

    public bool OnGpu => host.State.OnGpu;

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(float[] samples, string language, CancellationToken cancellation)
    {
        while (true)
        {
            try
            {
                return await host.TranscribeAsync(samples, language, cancellation);
            }
            catch (TranscriberFailedException)
            {
                // The host loads a fresh transcriber; the same phrase goes again.
            }
        }
    }

    /// <summary>The host belongs to the app.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// Recognizes a session's recorded audio with several engines on the same phrases, into
/// &lt;session&gt;\compare\&lt;time&gt;\: a jsonl per engine, summary.json and comparison.md with every phrase side by side.
/// The session's own transcript is not touched. A cloud error is tried once more, then noted for that phrase.
/// </summary>
public sealed class SessionComparison(Func<SpeechDetector> createDetector, TimeProvider time, ILogger logger, TimeSpan? pause = null)
{
    public const string FolderName = "compare";

    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Line = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger _logger = logger.ForContext<SessionComparison>();

    /// <param name="progress">Share of the audio processed, 0..1.</param>
    public async Task<ComparisonResult> RunAsync(
        SessionInfo session, string languageOfSchema1, IReadOnlyList<ComparedEngine> engines, IProgress<double> progress,
        CancellationToken cancellation)
    {
        SessionMeta meta = SessionMeta.Read(session, languageOfSchema1);
        string[] chunks = SessionPhrases.Chunks(session);
        string folder = Path.Combine(session.Folder, FolderName, time.GetLocalNow().ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(folder);
        _logger.Information("Comparing {Engines} on {Session}", string.Join(", ", engines.Select(e => e.Name)), session.Name);

        var rows = new List<(SpeechSegment Segment, string[] Texts)>();
        var elapsed = new TimeSpan[ engines.Count ];
        var errors = new int[ engines.Count ];
        foreach (SpeechSegment segment in SessionPhrases.Cut(chunks, meta, createDetector(), pause, progress))
        {
            // Every engine hears the phrase at the same time: the slowest sets the pace, not their sum.
            string[] texts = await Task.WhenAll(engines.Select(async (engine, i) =>
            {
                long started = Stopwatch.GetTimestamp();
                (string text, bool failed) = await RecognizeAsync(engine, segment, cancellation);
                elapsed[ i ] += Stopwatch.GetElapsedTime(started);
                if (failed)
                {
                    Interlocked.Increment(ref errors[ i ]);
                }

                return text;
            }));
            rows.Add((segment, texts));
        }

        var summaries = new List<ComparisonSummary>();
        for (int i = 0; i < engines.Count; i++)
        {
            await WriteLinesAsync(Path.Combine(folder, FileName(engines[ i ].Name) + ".jsonl"), rows, i, cancellation);
            summaries.Add(new ComparisonSummary(
                engines[ i ].Name,
                rows.Count(r => r.Texts[ i ].Length > 0 && !r.Texts[ i ].StartsWith('[')),
                errors[ i ],
                Math.Round(engines[ i ].Rubles?.Invoke() ?? 0, 4),
                Math.Round(elapsed[ i ].TotalSeconds, 1)));
        }

        JsonFiles.Write(Path.Combine(folder, "summary.json"), summaries);
        await File.WriteAllTextAsync(Path.Combine(folder, "comparison.md"), Markdown(session, summaries, rows), cancellation);
        _logger.Information("Comparison of {Session} written to {Folder}: {Phrases} phrases", session.Name, folder, rows.Count);
        return new ComparisonResult(folder, summaries);
    }

    /// <summary>A model id as a file name: openai/whisper-1 → openai__whisper-1.</summary>
    public static string FileName(string engine) => SessionFolders.Sanitize(engine.Replace("/", "__")) is { Length: > 0 } name ? name : "engine";

    private async Task<(string Text, bool Failed)> RecognizeAsync(ComparedEngine engine, SpeechSegment segment, CancellationToken cancellation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                IReadOnlyList<TranscriptSegment> recognized = await engine.Transcriber.TranscribeAsync(segment.Samples, segment.Language, cancellation);
                return (string.Join(" ", recognized.Select(s => s.Text).Where(t => t.Length > 0)), false);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or CloudTranscriptionException
                || (ex is TaskCanceledException && !cancellation.IsCancellationRequested))
            {
                string reason = ex is TaskCanceledException ? "не ответил вовремя" : ex.Message;
                _logger.Warning("{Engine} failed on {Start:0.00}-{End:0.00} s (attempt {Attempt}): {Reason}",
                    engine.Name, segment.Start.TotalSeconds, segment.End.TotalSeconds, attempt, reason);
                if (attempt == 2)
                {
                    return ($"[ошибка: {reason}]", true);
                }

                await Task.Delay(RetryAfter, cancellation);
            }
        }
    }

    private static async Task WriteLinesAsync(
        string file, List<(SpeechSegment Segment, string[] Texts)> rows, int engine, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        for (int seq = 0; seq < rows.Count; seq++)
        {
            SpeechSegment segment = rows[ seq ].Segment;
            text.AppendLine(JsonSerializer.Serialize(
                new
                {
                    seq = seq + 1,
                    start = Math.Round(segment.Start.TotalSeconds, 2),
                    end = Math.Round(segment.End.TotalSeconds, 2),
                    lang = segment.Language,
                    text = rows[ seq ].Texts[ engine ],
                },
                Line));
        }

        await File.WriteAllTextAsync(file, text.ToString(), cancellation);
    }

    private static string Markdown(SessionInfo session, List<ComparisonSummary> summaries, List<(SpeechSegment Segment, string[] Texts)> rows)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Сравнение распознавания: {session.Name}");
        md.AppendLine();
        md.AppendLine("| Движок | Фраз с текстом | Ошибок | ₽ | Время, с |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (ComparisonSummary s in summaries)
        {
            md.AppendLine($"| {s.Engine} | {s.Phrases} | {s.Errors} | {s.Rubles:0.00} | {s.Seconds:0.0} |");
        }

        foreach ((SpeechSegment segment, string[] texts) in rows)
        {
            md.AppendLine();
            md.AppendLine($"### {Clock(segment.Start)}–{Clock(segment.End)} ({segment.Language})");
            for (int i = 0; i < summaries.Count; i++)
            {
                md.AppendLine($"- **{summaries[ i ].Engine}**: {(texts[ i ].Length > 0 ? texts[ i ] : "—")}");
            }
        }

        return md.ToString();
    }

    private static string Clock(TimeSpan at) => $"{(int)at.TotalHours:00}:{at.Minutes:00}:{at.Seconds:00}";
}

/// <summary>The recorded audio of a session cut into phrases, with the languages of the marks in meta.json.</summary>
internal static class SessionPhrases
{
    private const int BlockSamples = 16000;

    /// <summary>The chunks in numeric order (999.wav before 1000.wav); none is an error: there is nothing to recognize.</summary>
    public static string[] Chunks(SessionInfo session)
    {
        string[] chunks = Directory.Exists(session.AudioFolder)
            ? [ .. Directory.EnumerateFiles(session.AudioFolder, "*.wav").OrderBy(ChunkNumber) ]
            : [];
        return chunks.Length > 0
            ? chunks
            : throw new InvalidOperationException($"В {session.AudioFolder} нет звука — распознавать нечего.");
    }

    public static IEnumerable<SpeechSegment> Cut(string[] chunks, SessionMeta meta, SpeechDetector detect, TimeSpan? pause, IProgress<double>? progress)
    {
        var segmenter = new SpeechSegmenter(detect, meta.LanguageMarks[ 0 ].Lang, pause: pause);
        foreach (LanguageMark mark in meta.LanguageMarks.Skip(1))
        {
            segmenter.Mark(TimeSpan.FromSeconds(mark.At), mark.Lang); // ahead of the audio: applied when it gets there
        }

        long total = chunks.Sum(c => new FileInfo(c).Length);
        long done = 0;
        foreach (string chunk in chunks)
        {
            float[] samples = WavSamples.Read(chunk);
            for (int at = 0; at < samples.Length; at += BlockSamples)
            {
                foreach (SpeechSegment segment in segmenter.Add(samples.AsSpan(at, Math.Min(BlockSamples, samples.Length - at))))
                {
                    yield return segment;
                }
            }

            done += new FileInfo(chunk).Length;
            progress?.Report((double)done / total);
        }

        foreach (SpeechSegment segment in segmenter.Flush())
        {
            yield return segment;
        }
    }

    private static int ChunkNumber(string path) =>
        int.TryParse(Path.GetFileNameWithoutExtension(path), out int number) ? number : int.MaxValue;
}

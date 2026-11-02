using Serilog;

namespace MicTap.Core;

public sealed record RetranscribeResult(IReadOnlyList<TranscriptLine> Lines, string? Backup);

/// <summary>
/// Recognizes a recorded session again from its audio\ folder: the old transcript.jsonl is kept as
/// transcript.&lt;yyyy-MM-dd_HH-mm-ss&gt;.bak, the chunks go through the VAD and Whisper with the languages of the
/// marks in meta.json. A failed transcriber is reloaded by the host and the segment retried.
/// </summary>
public sealed class SessionRetranscriber(
    TranscriberHost host, Func<SpeechDetector> createDetector, TimeProvider time, ILogger logger, TimeSpan? pause = null)
{
    private readonly ILogger _logger = logger.ForContext<SessionRetranscriber>();

    /// <param name="languageOfSchema1">Language of sessions recorded before v0.3, whose meta.json has no marks.</param>
    /// <param name="progress">Share of the audio processed, 0..1.</param>
    public async Task<RetranscribeResult> RunAsync(
        SessionInfo session, string languageOfSchema1, IProgress<double> progress, CancellationToken cancellation)
    {
        SessionMeta meta = SessionMeta.Read(session, languageOfSchema1);
        string[] chunks = SessionPhrases.Chunks(session);

        string transcript = Path.Combine(session.Folder, JsonlTranscriptSink.FileName);
        string? backup = null;
        if (File.Exists(transcript))
        {
            backup = Path.Combine(session.Folder, $"transcript.{time.GetLocalNow():yyyy-MM-dd_HH-mm-ss}.bak");
            File.Move(transcript, backup);
        }

        _logger.Information("Recognizing {Session} again from {Chunks} chunks; old transcript kept as {Backup}", session.Name, chunks.Length, backup);
        var sink = new JsonlTranscriptSink(session.Folder);
        foreach (SpeechSegment segment in SessionPhrases.Cut(chunks, meta, createDetector(), pause, progress))
        {
            await RecognizeAsync(sink, segment, cancellation);
        }

        _logger.Information("{Session} recognized again: {Lines} lines", session.Name, sink.Lines.Count);
        return new RetranscribeResult(sink.Lines, backup);
    }

    private async Task RecognizeAsync(JsonlTranscriptSink sink, SpeechSegment segment, CancellationToken cancellation)
    {
        IReadOnlyList<TranscriptSegment> recognized;
        while (true)
        {
            try
            {
                recognized = await host.TranscribeAsync(segment.Samples, segment.Language, cancellation);
                break;
            }
            catch (TranscriberFailedException)
            {
                // The host loads a fresh transcriber; the same segment goes again.
            }
        }

        string text = string.Join(" ", recognized.Select(s => s.Text).Where(t => t.Length > 0));
        if (WhisperArtifacts.IsArtifact(text))
        {
            _logger.Information(
                "Segment {Start:0.00}-{End:0.00} s left out: Whisper made up «{Text}»",
                segment.Start.TotalSeconds, segment.End.TotalSeconds, text);
        }
        else if (text.Length > 0)
        {
            sink.Append(Math.Round(segment.Start.TotalSeconds, 2), Math.Round(segment.End.TotalSeconds, 2), segment.Language, text);
        }
    }
}

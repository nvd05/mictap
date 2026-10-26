using System.Text.Json;

namespace MicTap.Core.Tests;

public sealed class JsonlTranscriptSinkTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly SessionInfo _session;

    public JsonlTranscriptSinkTests() => _session = new SessionInfo("2026-10-26_1600", _temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Each_chunk_is_one_line_with_schema_version_first()
    {
        var sink = new JsonlTranscriptSink(_session);

        sink.Put(new TranscriptLine(0, 0, 60, "Добрый день."));
        sink.Put(new TranscriptLine(1, 60, 95.5, "今日は"));

        string[] lines = File.ReadAllLines(Path.Combine(_temp.Path, "transcript.jsonl"));
        Assert.Equal(
            [
                """{"schemaVersion":1,"chunk":0,"start":0,"end":60,"text":"Добрый день."}""",
                """{"schemaVersion":1,"chunk":1,"start":60,"end":95.5,"text":"今日は"}""",
            ],
            lines);
    }

    [Fact]
    public void Edit_replaces_only_its_chunk()
    {
        var sink = new JsonlTranscriptSink(_session);
        sink.Put(new TranscriptLine(0, 0, 60, "первый"));
        sink.Put(new TranscriptLine(1, 60, 120, "второй"));
        sink.Put(new TranscriptLine(2, 120, 130, "третий"));

        sink.Put(new TranscriptLine(1, 60, 120, "второй, исправлен"));

        Assert.Equal(["первый", "второй, исправлен", "третий"], ReadTexts());
        Assert.False(File.Exists(sink.FilePath + ".tmp"));
    }

    [Fact]
    public void Reopened_session_keeps_its_lines_and_edits_them()
    {
        new JsonlTranscriptSink(_session).Put(new TranscriptLine(0, 0, 60, "было"));
        new JsonlTranscriptSink(_session).Put(new TranscriptLine(1, 60, 70, "дальше"));

        var reopened = new JsonlTranscriptSink(_session);
        reopened.Put(new TranscriptLine(0, 0, 60, "стало"));

        Assert.Equal(["стало", "дальше"], ReadTexts());
        Assert.Equal([0, 1], reopened.Lines.Select(l => l.Chunk));
    }

    private string[] ReadTexts() =>
        File.ReadAllLines(Path.Combine(_temp.Path, "transcript.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("text").GetString()!)
            .ToArray();
}

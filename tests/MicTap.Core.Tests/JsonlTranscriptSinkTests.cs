using System.Text.Json;

namespace MicTap.Core.Tests;

public sealed class JsonlTranscriptSinkTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string FilePath => Path.Combine(_temp.Path, "transcript.jsonl");

    [Fact]
    public void Each_segment_is_one_line_with_schema_version_first_and_seq_from_one()
    {
        var sink = new JsonlTranscriptSink(_temp.Path);

        sink.Append(1.25, 3.5, "ru", "Добрый день.");
        sink.Append(4, 6.75, "ja", "今日は");

        Assert.Equal(
            [
                """{"schemaVersion":2,"seq":1,"start":1.25,"end":3.5,"lang":"ru","text":"Добрый день.","edited":false}""",
                """{"schemaVersion":2,"seq":2,"start":4,"end":6.75,"lang":"ja","text":"今日は","edited":false}""",
            ],
            File.ReadAllLines(FilePath));
    }

    [Fact]
    public void Edit_rewrites_its_line_by_seq_and_marks_it_edited()
    {
        var sink = new JsonlTranscriptSink(_temp.Path);
        sink.Append(0, 1, "ru", "первый");
        sink.Append(1, 2, "ru", "второй");
        sink.Append(2, 3, "ja", "三番目");

        TranscriptLine edited = sink.Edit(2, "второй, исправлен");

        Assert.Equal(new TranscriptLine(2, 1, 2, "ru", "второй, исправлен", Edited: true), edited);
        Assert.Equal(
            [("первый", false), ("второй, исправлен", true), ("三番目", false)],
            File.ReadAllLines(FilePath).Select(Parse));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Reopened_transcript_round_trips_and_continues_seq()
    {
        var first = new JsonlTranscriptSink(_temp.Path);
        first.Append(0, 1.5, "ru", "было");
        first.Edit(1, "стало");

        var reopened = new JsonlTranscriptSink(_temp.Path);
        TranscriptLine next = reopened.Append(2, 3, "ja", "次");

        Assert.Equal(new TranscriptLine(1, 0, 1.5, "ru", "стало", Edited: true), reopened.Lines[0]);
        Assert.Equal(2, next.Seq);
    }

    [Fact]
    public void Transcript_of_schema_1_is_reported_not_mixed()
    {
        File.WriteAllText(FilePath, """{"schemaVersion":1,"chunk":0,"start":0,"end":60,"text":"старое"}""" + "\n");

        var error = Assert.Throws<InvalidDataException>(() => new JsonlTranscriptSink(_temp.Path));

        Assert.Contains("schemaVersion 1", error.Message);
    }

    [Fact]
    public void Editing_an_unknown_seq_is_an_error()
    {
        var sink = new JsonlTranscriptSink(_temp.Path);

        Assert.Throws<ArgumentOutOfRangeException>(() => sink.Edit(7, "нет такой"));
    }

    private static (string, bool) Parse(string json)
    {
        JsonElement root = JsonDocument.Parse(json).RootElement;
        return (root.GetProperty("text").GetString()!, root.GetProperty("edited").GetBoolean());
    }
}

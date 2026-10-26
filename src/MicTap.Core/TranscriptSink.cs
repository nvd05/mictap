using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicTap.Core;

/// <summary>Recognized text of one chunk; start and end are seconds from the session start.</summary>
public sealed record TranscriptLine(int Chunk, double Start, double End, string Text)
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion => CurrentSchemaVersion;
}

/// <summary>Receives the text of finished chunks and the user's edits of it.</summary>
public interface ITranscriptSink
{
    /// <summary>Adds the line of a chunk, or replaces the chunk's earlier line.</summary>
    void Put(TranscriptLine line);
}

/// <summary>
/// transcript.jsonl in the session folder: one JSON object per chunk, in chunk order. A new chunk is appended;
/// an edit rewrites the file through a temporary file and a rename. Thread-safe.
/// </summary>
public sealed class JsonlTranscriptSink : ITranscriptSink
{
    public const string FileName = "transcript.jsonl";

    private static readonly JsonSerializerOptions Options = new(JsonFiles.Options)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Cyrillic and kana readable for agents
    };

    private readonly Lock _gate = new();
    private readonly SortedDictionary<int, TranscriptLine> _lines = [];

    public JsonlTranscriptSink(SessionInfo session)
    {
        FilePath = Path.Combine(session.Folder, FileName);
        if (!File.Exists(FilePath))
        {
            return;
        }

        foreach (string json in File.ReadLines(FilePath).Where(l => l.Length > 0))
        {
            TranscriptLine line = JsonSerializer.Deserialize<TranscriptLine>(json, Options)
                ?? throw new InvalidDataException($"{FilePath} has a null line.");
            _lines[line.Chunk] = line;
        }
    }

    public string FilePath { get; }

    public IReadOnlyList<TranscriptLine> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines.Values];
            }
        }
    }

    public void Put(TranscriptLine line)
    {
        lock (_gate)
        {
            bool append = _lines.Count == 0 || line.Chunk > _lines.Keys.Max();
            _lines[line.Chunk] = line;
            if (append)
            {
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                Write(stream, line);
                stream.Flush(flushToDisk: true);
                return;
            }

            string temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (TranscriptLine kept in _lines.Values)
                {
                    Write(stream, kept);
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
    }

    private static void Write(Stream stream, TranscriptLine line)
    {
        JsonSerializer.Serialize(stream, line, Options);
        stream.WriteByte((byte)'\n');
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicTap.Core;

/// <summary>
/// Recognized text of one speech segment: seq numbers lines of a session from 1; start and end are seconds from
/// the session start; edited is set once the user corrects the text.
/// </summary>
public sealed record TranscriptLine(int Seq, double Start, double End, string Lang, string Text, bool Edited)
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion => CurrentSchemaVersion;
}

/// <summary>Receives recognized segments and the user's corrections of them.</summary>
public interface ITranscriptSink
{
    /// <summary>Adds a recognized segment under the next seq.</summary>
    TranscriptLine Append(double start, double end, string lang, string text);

    /// <summary>Replaces the text of line <paramref name="seq"/> and marks it edited.</summary>
    TranscriptLine Edit(int seq, string text);
}

/// <summary>
/// transcript.jsonl in the session folder: one JSON object per segment, in seq order. A new segment is appended;
/// an edit rewrites the file through a temporary file and a rename. Thread-safe.
/// </summary>
public sealed class JsonlTranscriptSink : ITranscriptSink
{
    public const string FileName = "transcript.jsonl";

    private static readonly JsonSerializerOptions Options = new(JsonFiles.Options) { WriteIndented = false };

    private readonly Lock _gate = new();
    private readonly List<TranscriptLine> _lines = [];

    public JsonlTranscriptSink(string sessionFolder)
    {
        FilePath = Path.Combine(sessionFolder, FileName);
        if (!File.Exists(FilePath))
        {
            return;
        }

        int number = 0;
        foreach (string json in File.ReadLines(FilePath))
        {
            number++;
            if (json.Length == 0)
            {
                continue;
            }

            using (JsonDocument document = JsonDocument.Parse(json))
            {
                int version = document.RootElement.TryGetProperty("schemaVersion", out JsonElement v) ? v.GetInt32() : 0;
                if (version != TranscriptLine.CurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"{FilePath}, line {number}: schemaVersion {version}, expected {TranscriptLine.CurrentSchemaVersion}.");
                }
            }

            _lines.Add(JsonSerializer.Deserialize<TranscriptLine>(json, Options)!);
        }
    }

    public string FilePath { get; }

    public IReadOnlyList<TranscriptLine> Lines
    {
        get
        {
            lock (_gate)
            {
                return [ .. _lines ];
            }
        }
    }

    public TranscriptLine Append(double start, double end, string lang, string text)
    {
        lock (_gate)
        {
            var line = new TranscriptLine(_lines.Count == 0 ? 1 : _lines[ ^1 ].Seq + 1, start, end, lang, text, Edited: false);
            using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                Write(stream, line);
                stream.Flush(flushToDisk: true);
            }

            _lines.Add(line);
            return line;
        }
    }

    public TranscriptLine Edit(int seq, string text)
    {
        lock (_gate)
        {
            int index = _lines.FindIndex(l => l.Seq == seq);
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(seq), seq, $"{FilePath} has no line {seq}.");
            }

            TranscriptLine edited = _lines[ index ] with { Text = text, Edited = true };
            string temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (int i = 0; i < _lines.Count; i++)
                {
                    Write(stream, i == index ? edited : _lines[ i ]);
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, FilePath, overwrite: true);
            _lines[ index ] = edited;
            return edited;
        }
    }

    private static void Write(Stream stream, TranscriptLine line)
    {
        JsonSerializer.Serialize(stream, line, Options);
        stream.WriteByte((byte)'\n');
    }
}

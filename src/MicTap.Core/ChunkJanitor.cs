using Serilog;

namespace MicTap.Core;

/// <summary>
/// Audio retention deleteAfterTranscript for one recording: a closed chunk is deleted once all speech it holds is in
/// transcript.jsonl on disk — the segmenter has moved past the chunk's end and no segment that started before it
/// waits for recognition. Thread-safe: chunks close on the writer thread, segments come from the pipeline.
/// </summary>
internal sealed class ChunkJanitor(ILogger logger)
{
    private readonly Lock _gate = new();
    private readonly List<(string Path, TimeSpan End)> _closed = [ ];
    private readonly Queue<TimeSpan> _unwritten = new();
    private TimeSpan _committed;

    public void ChunkClosed(string path, TimeSpan end)
    {
        lock (_gate)
        {
            _closed.Add((path, end));
            Sweep();
        }
    }

    /// <summary>The segmenter emitted <paramref name="segments"/> and will not use audio before <paramref name="committed"/> again.</summary>
    public void Segmented(IEnumerable<SpeechSegment> segments, TimeSpan committed)
    {
        lock (_gate)
        {
            foreach (SpeechSegment segment in segments)
            {
                _unwritten.Enqueue(segment.Start);
            }

            _committed = committed;
            Sweep();
        }
    }

    /// <summary>The oldest emitted segment is in transcript.jsonl (or had no words).</summary>
    public void Written()
    {
        lock (_gate)
        {
            _unwritten.Dequeue();
            Sweep();
        }
    }

    private void Sweep()
    {
        TimeSpan safe = _unwritten.Count > 0 && _unwritten.Peek() < _committed ? _unwritten.Peek() : _committed;
        for (int i = 0; i < _closed.Count;)
        {
            (string path, TimeSpan end) = _closed[i];
            if (end > safe)
            {
                i++;
                continue;
            }

            try
            {
                File.Delete(path);
                logger.Information("Chunk {Chunk} deleted: its speech is in the transcript", path);
                _closed.RemoveAt(i);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Warning(ex, "Chunk {Chunk} could not be deleted yet; trying again later", path);
                i++;
            }
        }
    }
}

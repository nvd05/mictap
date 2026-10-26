namespace MicTap.Core;

/// <summary>A chunk that got its final header and was closed.</summary>
public sealed record ChunkInfo(int Index, string Path, long DataBytes);

/// <summary>
/// Splits a continuous PCM stream into numbered WAV chunks of fixed duration: 000.wav, 001.wav, ...
/// An open chunk keeps zero sizes in its header (see <see cref="WavHeader"/>); <see cref="Flush"/> pushes
/// written audio to disk. On rotation the next chunk is created before the previous one is closed, and a
/// write that crosses the boundary is split at the exact byte, so chunks join without a gap. Not thread-safe.
/// </summary>
public sealed class ChunkedWavWriter : IDisposable
{
    private readonly string _folder;
    private readonly PcmFormat _format;
    private readonly long _chunkBytes;
    private FileStream _current;
    private long _currentBytes;
    private bool _disposed;

    public ChunkedWavWriter(string folder, PcmFormat format, int chunkSeconds, int firstIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSeconds);
        _folder = folder;
        _format = format;
        _chunkBytes = (long)format.ByteRate * chunkSeconds;
        Directory.CreateDirectory(folder);
        CurrentIndex = firstIndex;
        _current = Open(firstIndex);
    }

    /// <summary>Raised after a chunk got its final header; on rotation the next chunk already exists.</summary>
    public event EventHandler<ChunkInfo>? ChunkClosed;

    public int CurrentIndex { get; private set; }

    /// <summary>Audio bytes written across all chunks.</summary>
    public long TotalBytes { get; private set; }

    public static string FileName(int index) => $"{index:D3}.wav";

    public void Write(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!pcm.IsEmpty)
        {
            if (_currentBytes == _chunkBytes)
            {
                Rotate();
            }

            int count = (int)Math.Min(pcm.Length, _chunkBytes - _currentBytes);
            _current.Write(pcm[ ..count ]);
            _currentBytes += count;
            TotalBytes += count;
            pcm = pcm[ count.. ];
        }
    }

    /// <summary>Pushes everything written so far to disk; the header keeps zero sizes until close.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _current.Flush(flushToDisk: true);
    }

    /// <summary>Closes the current chunk with its final header.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close(_current, CurrentIndex, _currentBytes);
    }

    private void Rotate()
    {
        FileStream next = Open(CurrentIndex + 1);
        FileStream previous = _current;
        int previousIndex = CurrentIndex;
        long previousBytes = _currentBytes;
        _current = next;
        CurrentIndex++;
        _currentBytes = 0;
        Close(previous, previousIndex, previousBytes);
    }

    private FileStream Open(int index)
    {
        var stream = new FileStream(ChunkPath(index), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(WavHeader.CreateOpen(_format));
        stream.Flush(flushToDisk: true);
        return stream;
    }

    private void Close(FileStream stream, int index, long dataBytes)
    {
        using (stream)
        {
            WavHeader.WriteSizes(stream, dataBytes);
            stream.Flush(flushToDisk: true);
        }

        ChunkClosed?.Invoke(this, new ChunkInfo(index, ChunkPath(index), dataBytes));
    }

    private string ChunkPath(int index) => Path.Combine(_folder, FileName(index));
}

using System.Buffers.Binary;

namespace MicTap.Core;

public enum WavRepairResult
{
    /// <summary>Header sizes are final.</summary>
    Intact,

    /// <summary>The chunk was left open (zero sizes); sizes were rebuilt from the file length.</summary>
    Repaired,

    /// <summary>Not the canonical header mictap writes; the file was left untouched.</summary>
    NotRecognized,
}

/// <summary>
/// Canonical 44-byte PCM WAV header: RIFF, a 16-byte fmt chunk and the data chunk.
/// A chunk that is still being written carries zero RIFF and data sizes; they are filled in on close.
/// </summary>
public static class WavHeader
{
    public const int Size = 44;

    private const int RiffSizeOffset = 4;
    private const int DataSizeOffset = 40;
    private const long MaxDataSize = uint.MaxValue - (Size - 8);

    /// <summary>Header of an open chunk: RIFF and data sizes are zero.</summary>
    public static byte[] CreateOpen(PcmFormat format)
    {
        var header = new byte[Size];
        Span<byte> span = header;
        "RIFF"u8.CopyTo(span);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1); // WAVE_FORMAT_PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)format.ByteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], (ushort)format.BitsPerSample);
        "data"u8.CopyTo(span[36..]);
        return header;
    }

    /// <summary>Writes the final RIFF and data sizes into the header at the start of <paramref name="stream"/>.</summary>
    public static void WriteSizes(Stream stream, long dataSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dataSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dataSize, MaxDataSize);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)(dataSize + Size - 8));
        stream.Position = RiffSizeOffset;
        stream.Write(value);
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)dataSize);
        stream.Position = DataSizeOffset;
        stream.Write(value);
    }

    /// <summary>
    /// Gives a chunk left open by a crash (zero sizes in the header) the sizes of the audio actually on disk.
    /// A trailing partial sample, if any, is cut off.
    /// </summary>
    public static WavRepairResult RepairIfOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length < Size)
        {
            return WavRepairResult.NotRecognized;
        }

        Span<byte> header = stackalloc byte[Size];
        stream.ReadExactly(header);
        if (!IsCanonical(header))
        {
            return WavRepairResult.NotRecognized;
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header[RiffSizeOffset..]);
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(header[DataSizeOffset..]);
        if (riffSize != 0 && dataSize != 0)
        {
            return WavRepairResult.Intact;
        }

        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
        long audio = stream.Length - Size;
        audio -= audio % blockAlign;
        if (riffSize == audio + Size - 8 && dataSize == audio)
        {
            return WavRepairResult.Intact; // a closed chunk that holds no audio
        }

        stream.SetLength(Size + audio);
        WriteSizes(stream, audio);
        stream.Flush(flushToDisk: true);
        return WavRepairResult.Repaired;
    }

    private static bool IsCanonical(ReadOnlySpan<byte> header) =>
        header[..4].SequenceEqual("RIFF"u8)
        && header[8..12].SequenceEqual("WAVE"u8)
        && header[12..16].SequenceEqual("fmt "u8)
        && BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) == 16
        && BinaryPrimitives.ReadUInt16LittleEndian(header[20..]) == 1
        && BinaryPrimitives.ReadUInt16LittleEndian(header[32..]) != 0
        && header[36..40].SequenceEqual("data"u8);
}

using System.Buffers.Binary;

namespace MicTap.Core;

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
}

using System.Buffers.Binary;
using NAudio.Wave;

namespace MicTap.Core.Tests;

/// <summary>Reads WAV files back through NAudio, independently of the code under test.</summary>
internal static class WavFiles
{
    /// <summary>Audio bytes of a closed chunk; fails unless the header is final and the format is 16 kHz mono 16-bit.</summary>
    public static byte[] ReadAudio(string path)
    {
        using var reader = new WaveFileReader(path);
        Assert.Equal(new WaveFormat(16000, 16, 1), reader.WaveFormat);
        Assert.Equal(new FileInfo(path).Length - WavHeader.Size, reader.Length);
        var audio = new byte[ reader.Length ];
        reader.ReadExactly(audio);
        return audio;
    }

    public static byte[] ReadAudio(IEnumerable<string> paths) => paths.SelectMany(ReadAudio).ToArray();

    /// <summary>RIFF and data sizes as stored in the header; readable while the writer keeps the file open.</summary>
    public static (uint Riff, uint Data) HeaderSizes(string path)
    {
        byte[] header = new byte[ WavHeader.Size ];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.ReadExactly(header);
        }

        return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
    }

    /// <summary>File length as the file system reports it, while the writer keeps the file open.</summary>
    public static long LengthOnDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Length;
    }

    public static string[] Chunks(string audioFolder) =>
        Directory.GetFiles(audioFolder, "*.wav").Order(StringComparer.Ordinal).ToArray();
}

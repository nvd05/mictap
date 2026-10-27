using System.Buffers.Binary;
using System.Text;

namespace MicTap.Core;

/// <summary>Reads a closed 16 kHz mono 16-bit WAV file as float samples, -1..1, for recognition.</summary>
public static class WavSamples
{
    public static float[] Read(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 12 || Tag(file, 0) != "RIFF" || Tag(file, 8) != "WAVE")
        {
            throw new InvalidDataException($"{path} is not a WAV file.");
        }

        PcmFormat? format = null;
        for (int at = 12; at + 8 <= file.Length;)
        {
            string tag = Tag(file, at);
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4)), (uint)(file.Length - at - 8));
            ReadOnlySpan<byte> body = file.AsSpan(at + 8, size);
            if (tag == "fmt ")
            {
                format = new PcmFormat(
                    BinaryPrimitives.ReadInt32LittleEndian(body[ 4.. ]),
                    BinaryPrimitives.ReadInt16LittleEndian(body[ 2.. ]),
                    BinaryPrimitives.ReadInt16LittleEndian(body[ 14.. ]));
            }
            else if (tag == "data")
            {
                if (format != PcmFormat.Mono16k)
                {
                    throw new InvalidDataException($"{path} is {format}, recognition needs {PcmFormat.Mono16k}.");
                }

                return FromPcm16(body);
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path} has no data chunk.");
    }

    /// <summary>A complete 16 kHz mono 16-bit WAV file of <paramref name="samples"/>, -1..1 (clipped).</summary>
    public static byte[] ToWav(ReadOnlySpan<float> samples)
    {
        var wav = new byte[ WavHeader.Size + samples.Length * 2 ];
        WavHeader.CreateOpen(PcmFormat.Mono16k).CopyTo(wav, 0);
        for (int i = 0; i < samples.Length; i++)
        {
            short value = (short)Math.Round(Math.Clamp(samples[ i ], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(WavHeader.Size + i * 2), value);
        }

        using var stream = new MemoryStream(wav);
        WavHeader.WriteSizes(stream, samples.Length * 2L);
        return wav;
    }

    /// <summary>16-bit little-endian PCM as floats, -1..1.</summary>
    public static float[] FromPcm16(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[ pcm.Length / 2 ];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[ i ] = BinaryPrimitives.ReadInt16LittleEndian(pcm[ (i * 2).. ]) / 32768f;
        }

        return samples;
    }

    private static string Tag(byte[] file, int at) => Encoding.ASCII.GetString(file, at, 4);
}

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
                    BinaryPrimitives.ReadInt32LittleEndian(body[4..]),
                    BinaryPrimitives.ReadInt16LittleEndian(body[2..]),
                    BinaryPrimitives.ReadInt16LittleEndian(body[14..]));
            }
            else if (tag == "data")
            {
                if (format != PcmFormat.Mono16k)
                {
                    throw new InvalidDataException($"{path} is {format}, recognition needs {PcmFormat.Mono16k}.");
                }

                var ring = new AudioRing(size / 2);
                ring.Write(body);
                return ring.Snapshot();
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path} has no data chunk.");
    }

    private static string Tag(byte[] file, int at) => Encoding.ASCII.GetString(file, at, 4);
}

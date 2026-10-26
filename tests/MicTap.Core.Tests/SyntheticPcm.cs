using System.Buffers.Binary;

namespace MicTap.Core.Tests;

/// <summary>16 kHz mono 16-bit PCM for tests, generated without a microphone.</summary>
internal static class SyntheticPcm
{
    /// <summary>
    /// Samples step by 7, so the sequence does not repeat for 65536 samples (4 s): a lost, repeated or
    /// reordered sample breaks byte equality with the original.
    /// </summary>
    public static byte[] Create(TimeSpan duration)
    {
        int samples = (int)(PcmFormat.Mono16k.SampleRate * duration.TotalSeconds);
        var pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), unchecked((short)((i * 7) + 1)));
        }

        return pcm;
    }

    /// <summary>Cuts <paramref name="pcm"/> into consecutive pieces, cycling through <paramref name="sizes"/>.</summary>
    public static IEnumerable<byte[]> Split(byte[] pcm, params int[] sizes)
    {
        int offset = 0;
        for (int n = 0; offset < pcm.Length; n++)
        {
            int size = Math.Min(sizes[n % sizes.Length], pcm.Length - offset);
            yield return pcm[offset..(offset + size)];
            offset += size;
        }
    }
}

using System.Buffers.Binary;

namespace MicTap.Core;

/// <summary>Input level for the meter.</summary>
public static class AudioLevel
{
    private const double FloorDb = -60;

    /// <summary>Peak absolute amplitude of 16-bit PCM, 0..1.</summary>
    public static double Peak(ReadOnlySpan<byte> pcm16)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm16[ i.. ])));
        }

        return peak / 32768.0;
    }

    /// <summary>Maps a linear peak to 0..1 on a -60..0 dBFS scale, closer to perceived loudness than the raw peak.</summary>
    public static double ToMeter(double peak) =>
        peak <= 0 ? 0 : Math.Clamp(1 - (20 * Math.Log10(peak) / FloorDb), 0, 1);
}

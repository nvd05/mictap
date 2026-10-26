using System.Buffers.Binary;

namespace MicTap.Core.Tests;

public sealed class AudioRingTests
{
    [Fact]
    public void Keeps_everything_while_below_capacity()
    {
        var ring = new AudioRing(10);

        ring.Write(Pcm(1, 2, 3));

        Assert.Equal(Floats(1, 2, 3), ring.Snapshot());
        Assert.Equal(3, ring.Written);
    }

    [Fact]
    public void Keeps_the_latest_samples_oldest_first_once_full()
    {
        var ring = new AudioRing(4);

        ring.Write(Pcm(1, 2, 3));
        ring.Write(Pcm(4, 5, 6));
        ring.Write(Pcm(7));

        Assert.Equal(Floats(4, 5, 6, 7), ring.Snapshot());
        Assert.Equal(7, ring.Written);
    }

    [Fact]
    public void Converts_full_scale_16_bit_to_minus_one_to_one()
    {
        var ring = new AudioRing(2);

        ring.Write(Pcm(short.MinValue, short.MaxValue));

        Assert.Equal([ -1f, 32767 / 32768f ], ring.Snapshot());
    }

    [Fact]
    public void Ten_seconds_at_16_kHz_hold_160000_samples()
    {
        AudioRing ring = AudioRing.For(PcmFormat.Mono16k, TimeSpan.FromSeconds(10));

        ring.Write(new byte[ 2 * 200_000 ]);

        Assert.Equal(160_000, ring.Snapshot().Length);
    }

    [Fact]
    public void Clear_starts_over()
    {
        var ring = new AudioRing(4);
        ring.Write(Pcm(1, 2, 3, 4, 5));

        ring.Clear();
        ring.Write(Pcm(9));

        Assert.Equal(Floats(9), ring.Snapshot());
        Assert.Equal(1, ring.Written);
    }

    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[ samples.Length * 2 ];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[ i ]);
        }

        return bytes;
    }

    private static float[] Floats(params short[] samples) => samples.Select(s => s / 32768f).ToArray();
}

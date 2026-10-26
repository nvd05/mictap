using System.Buffers.Binary;

namespace MicTap.Core;

/// <summary>
/// The latest samples of a 16-bit PCM stream as floats, -1..1; older samples are overwritten.
/// Not thread-safe: one thread writes and takes snapshots.
/// </summary>
public sealed class AudioRing(int capacity)
{
    private readonly float[] _samples = new float[ capacity ];
    private int _next;

    /// <summary>Samples written since creation or the last <see cref="Clear"/>, including overwritten ones.</summary>
    public long Written { get; private set; }

    public int Count => (int)Math.Min(Written, _samples.Length);

    public static AudioRing For(PcmFormat format, TimeSpan length) => new((int)(format.SampleRate * length.TotalSeconds));

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            _samples[ _next ] = BinaryPrimitives.ReadInt16LittleEndian(pcm16[ i.. ]) / 32768f;
            _next = (_next + 1) % _samples.Length;
        }

        Written += pcm16.Length / 2;
    }

    /// <summary>The kept samples, oldest first.</summary>
    public float[] Snapshot()
    {
        var copy = new float[ Count ];
        int start = Count < _samples.Length ? 0 : _next;
        int tail = Math.Min(Count, _samples.Length - start);
        Array.Copy(_samples, start, copy, 0, tail);
        Array.Copy(_samples, 0, copy, tail, Count - tail);
        return copy;
    }

    public void Clear()
    {
        _next = 0;
        Written = 0;
    }
}

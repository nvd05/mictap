namespace MicTap.Core;

/// <summary>A segment of speech; start and end are offsets from the start of the stream.</summary>
public sealed record SpeechSegment(TimeSpan Start, TimeSpan End, string Language, float[] Samples);

/// <summary>
/// Cuts a stream of 16 kHz mono samples into segments of speech. A segment ends after <see cref="MinSilence"/> of
/// silence, after <see cref="MaxSegment"/> of speech, or at a language mark; silence never becomes a segment.
/// Each segment carries the language in effect at its start. Not thread-safe.
/// </summary>
public sealed class SpeechSegmenter
{
    public static readonly TimeSpan MinSilence = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan MaxSegment = TimeSpan.FromSeconds(30);

    private const int Rate = 16000;
    private static readonly int MinSilenceSamples = ToSamples(MinSilence);
    private static readonly int MaxSegmentSamples = ToSamples(MaxSegment);
    private static readonly int DetectEverySamples = ToSamples(TimeSpan.FromMilliseconds(500));
    private static readonly int KeepWithoutSpeechSamples = ToSamples(TimeSpan.FromSeconds(1));
    private static readonly int LeadSamples = ToSamples(TimeSpan.FromMilliseconds(200));

    private readonly Func<float[], IReadOnlyList<SpeechSpan>> _detect;
    private readonly List<(long Position, string Language)> _marks = [];
    private float[] _pending = new float[Rate * 40];
    private int _count;
    private long _pendingStart;
    private long _detectedAt;

    public SpeechSegmenter(Func<float[], IReadOnlyList<SpeechSpan>> detect, string language)
    {
        _detect = detect;
        Language = language;
    }

    /// <summary>The language of the next segment.</summary>
    public string Language { get; private set; }

    public TimeSpan Received => FromSamples(ReceivedSamples);

    private long ReceivedSamples => _pendingStart + _count;

    public IReadOnlyList<SpeechSegment> Add(ReadOnlySpan<float> samples)
    {
        var output = new List<SpeechSegment>();
        while (_marks.Count > 0 && _marks[0].Position < ReceivedSamples + samples.Length)
        {
            int before = (int)Math.Max(0, _marks[0].Position - ReceivedSamples);
            Append(samples[..before]);
            samples = samples[before..];
            string language = _marks[0].Language;
            _marks.RemoveAt(0);
            CutAll(output, _count, language);
        }

        Append(samples);
        if (ReceivedSamples - _detectedAt >= DetectEverySamples)
        {
            Detect(output, _count, closeAll: false);
        }

        return output;
    }

    /// <summary>
    /// A hard boundary at <paramref name="at"/>: speech before it is cut off into segments, later speech gets
    /// <paramref name="language"/>. A mark ahead of the received audio waits until the audio reaches it.
    /// </summary>
    public IReadOnlyList<SpeechSegment> Mark(TimeSpan at, string language)
    {
        var output = new List<SpeechSegment>();
        long position = ToSamples(at);
        if (position > ReceivedSamples)
        {
            _marks.Add((position, language));
            _marks.Sort((a, b) => a.Position.CompareTo(b.Position));
            return output;
        }

        CutAll(output, (int)Math.Clamp(position - _pendingStart, 0, _count), language);
        return output;
    }

    /// <summary>End of the stream: the remaining speech becomes segments.</summary>
    public IReadOnlyList<SpeechSegment> Flush()
    {
        var output = new List<SpeechSegment>();
        Detect(output, _count, closeAll: true);
        return output;
    }

    private void CutAll(List<SpeechSegment> output, int length, string language)
    {
        Detect(output, length, closeAll: true);
        Language = language;
    }

    private void Detect(List<SpeechSegment> output, int length, bool closeAll)
    {
        float[] window = _pending.AsSpan(0, length).ToArray();
        int consumed = 0;
        bool speechGoesOn = false;
        foreach (SpeechSpan span in _detect(window))
        {
            int start = Math.Clamp(ToSamples(span.Start), consumed, length);
            int end = Math.Clamp(ToSamples(span.End), start, length);
            if (closeAll || end <= length - MinSilenceSamples)
            {
                Emit(output, window, start, end);
                consumed = end;
                continue;
            }

            // Speech reaches the end of the audio: wait for its end, but never longer than MaxSegment.
            while (length - start >= MaxSegmentSamples)
            {
                Emit(output, window, start, start + MaxSegmentSamples);
                start += MaxSegmentSamples;
                consumed = start;
            }

            consumed = Math.Max(consumed, start - LeadSamples);
            speechGoesOn = true;
            break;
        }

        if (closeAll)
        {
            consumed = length;
        }
        else if (!speechGoesOn)
        {
            consumed = Math.Max(consumed, length - KeepWithoutSpeechSamples);
        }

        Drop(consumed);
        _detectedAt = ReceivedSamples;
    }

    private void Emit(List<SpeechSegment> output, float[] window, int start, int end)
    {
        for (int from = start; from < end; from += MaxSegmentSamples)
        {
            int to = Math.Min(end, from + MaxSegmentSamples);
            output.Add(new SpeechSegment(
                FromSamples(_pendingStart + from), FromSamples(_pendingStart + to), Language, window[from..to]));
        }
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        if (_count + samples.Length > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _count + samples.Length));
        }

        samples.CopyTo(_pending.AsSpan(_count));
        _count += samples.Length;
    }

    private void Drop(int samples)
    {
        Array.Copy(_pending, samples, _pending, 0, _count - samples);
        _count -= samples;
        _pendingStart += samples;
    }

    private static int ToSamples(TimeSpan time) => (int)Math.Round(time.TotalSeconds * Rate);

    private static TimeSpan FromSamples(long samples) => TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / Rate);
}

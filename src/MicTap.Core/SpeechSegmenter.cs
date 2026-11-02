namespace MicTap.Core;

/// <summary>A segment of speech; start and end are offsets from the start of the stream.</summary>
public sealed record SpeechSegment(TimeSpan Start, TimeSpan End, string Language, float[] Samples);

/// <summary>
/// Cuts a stream of 16 kHz mono samples into phrases of speech. A phrase ends after a pause of silence, at a language
/// mark, at a <see cref="Flush"/>, or before it grows past <see cref="MaxSegment"/>: then it ends at the last pause
/// between words, and only speech without any such pause is cut at <see cref="MaxSegment"/>. Silence never becomes a segment.
/// Each segment carries the language in effect at its start. Not thread-safe.
/// </summary>
public sealed class SpeechSegmenter
{
    /// <summary>Silence that ends a span of speech for the VAD; a phrase pause is never shorter.</summary>
    public static readonly TimeSpan MinSilence = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultPause = MinSilence;
    public static readonly TimeSpan MaxSegment = TimeSpan.FromSeconds(30);

    private const int Rate = 16000;
    private static readonly int MaxSegmentSamples = ToSamples(MaxSegment);
    private static readonly int DetectEverySamples = ToSamples(TimeSpan.FromMilliseconds(500));
    private static readonly int KeepWithoutSpeechSamples = ToSamples(TimeSpan.FromSeconds(1));
    private static readonly int LeadSamples = ToSamples(TimeSpan.FromMilliseconds(200));

    private readonly SpeechDetector _detect;
    private readonly int _pauseSamples;
    private readonly List<(long Position, string Language)> _marks = [];
    private float[] _pending = new float[ Rate * 40 ];
    private int _count;
    private long _pendingStart;
    private long _detectedAt;

    /// <param name="origin">Stream time of the first sample: an appended recording continues its session.</param>
    /// <param name="pause">Silence that ends a phrase; <see cref="DefaultPause"/> when null, at least <see cref="MinSilence"/>.</param>
    public SpeechSegmenter(SpeechDetector detect, string language, TimeSpan origin = default, TimeSpan? pause = null)
    {
        _detect = detect;
        Language = language;
        _pauseSamples = ToSamples(TimeSpan.FromTicks(Math.Max((pause ?? DefaultPause).Ticks, MinSilence.Ticks)));
        _pendingStart = ToSamples(origin);
        _detectedAt = _pendingStart;
    }

    /// <summary>The language of the next segment.</summary>
    public string Language { get; private set; }

    public TimeSpan Received => FromSamples(ReceivedSamples);

    /// <summary>Audio before this point will never be part of a later segment.</summary>
    public TimeSpan Committed => FromSamples(_pendingStart);

    private long ReceivedSamples => _pendingStart + _count;

    public IReadOnlyList<SpeechSegment> Add(ReadOnlySpan<float> samples)
    {
        var output = new List<SpeechSegment>();
        while (_marks.Count > 0 && _marks[ 0 ].Position < ReceivedSamples + samples.Length)
        {
            int before = (int)Math.Max(0, _marks[ 0 ].Position - ReceivedSamples);
            Append(samples[ ..before ]);
            samples = samples[ before.. ];
            string language = _marks[ 0 ].Language;
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

    /// <summary>
    /// Every phrase ends now, without waiting for its pause: the remaining speech becomes segments. At the end of the
    /// stream, or in the middle of it when the text is wanted at once; later audio goes on as usual.
    /// </summary>
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
        (int Start, int End)? phrase = null;
        foreach (SpeechSpan span in _detect(window))
        {
            int start = Math.Clamp(ToSamples(span.Start), consumed, length);
            int end = Math.Clamp(ToSamples(span.End), start, length);
            if (phrase is { } open && (start - open.End >= _pauseSamples || end - open.Start >= MaxSegmentSamples))
            {
                // A pause ends the phrase; so does speech that would make it too long: it ends at the pause before.
                Emit(output, window, open.Start, open.End);
                consumed = open.End;
                phrase = null;
            }

            phrase = (phrase?.Start ?? start, end);
        }

        bool speechGoesOn = false;
        if (phrase is { } last)
        {
            if (closeAll || last.End <= length - _pauseSamples)
            {
                Emit(output, window, last.Start, last.End);
                consumed = last.End;
            }
            else if (length - last.Start < MaxSegmentSamples)
            {
                // The phrase reaches the end of the audio: wait for the pause.
                consumed = Math.Max(consumed, last.Start - LeadSamples);
                speechGoesOn = true;
            }
            else if (last.End - last.Start < MaxSegmentSamples)
            {
                // No time left to wait for the pause: the phrase ends at the shorter silence after it.
                Emit(output, window, last.Start, last.End);
                consumed = last.End;
            }
            else
            {
                // Speech without a single pause for MaxSegment: cut it there.
                int start = last.Start;
                while (length - start >= MaxSegmentSamples)
                {
                    Emit(output, window, start, start + MaxSegmentSamples);
                    start += MaxSegmentSamples;
                    consumed = start;
                }

                consumed = Math.Max(consumed, start - LeadSamples);
                speechGoesOn = true;
            }
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
                FromSamples(_pendingStart + from), FromSamples(_pendingStart + to), Language, window[ from..to ]));
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

namespace MicTap.Core.Tests;

public sealed class SessionMetaTests
{
    [Fact]
    public void Session_seconds_map_to_the_clock_of_the_recording_that_holds_them()
    {
        var evening = new DateTimeOffset(2026, 10, 27, 21, 39, 9, TimeSpan.FromHours(5));
        var morning = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(5));
        var meta = new SessionMeta(
            [ new SessionStart(evening, 0, 600), new SessionStart(morning, 600.5) ], "Mic", MetaFormat.From(PcmFormat.Mono16k), 60, "test",
            [ new LanguageMark(0, "ru") ]);

        Assert.Equal(evening, meta.Started);
        Assert.Equal(evening.AddSeconds(90), meta.At(90));
        Assert.Equal(morning.AddSeconds(30), meta.At(630.5));
        Assert.Equal(TimeSpan.FromHours(5), meta.At(630.5)!.Value.Offset);
    }
}

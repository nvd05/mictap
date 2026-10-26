using Serilog.Core;

namespace MicTap.Core.Tests;

public sealed class SessionRetranscriberTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 21, 0, 5, TimeSpan.FromHours(5));

    private readonly TempFolder _temp = new();
    private readonly SessionInfo _session;
    private readonly TranscriberHost _host;

    public SessionRetranscriberTests()
    {
        _session = new SessionInfo("2026-10-26_20-00-00", _temp.Path);
        Directory.CreateDirectory(_session.AudioFolder);
        _host = new TranscriberHost(
            _ => Task.FromResult<ITranscriber>(FakeTranscriber.Saying((samples, language) => $"{language}:{samples.Length / 16}ms")),
            Logger.None, TimeProvider.System);
        _host.Start();
    }

    public void Dispose()
    {
        _host.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Audio_is_recognized_again_with_languages_from_meta_and_the_old_transcript_is_kept()
    {
        WriteChunks(chunkSeconds: 2, FakeSpeech.SpeechPcm(1.5), FakeSpeech.SilencePcm(1), FakeSpeech.SpeechPcm(2), FakeSpeech.SilencePcm(1));
        WriteMeta(new LanguageMark(0, "ru"), new LanguageMark(3.5, "ja"));
        File.WriteAllText(Path.Combine(_temp.Path, "transcript.jsonl"), "старый текст\n");

        RetranscribeResult result = await Retranscriber().RunAsync(_session, "ru", new Progress<double>(), CancellationToken.None);

        Assert.Equal(
            [ (1, 0.0, 1.5, "ru", "ru:1500ms"), (2, 2.5, 3.5, "ru", "ru:1000ms"), (3, 3.5, 4.5, "ja", "ja:1000ms") ],
            result.Lines.Select(l => (l.Seq, l.Start, l.End, l.Lang, l.Text)));
        Assert.Equal(Path.Combine(_temp.Path, "transcript.2026-10-26_21-00-05.bak"), result.Backup);
        Assert.Equal("старый текст\n", File.ReadAllText(result.Backup!));
        Assert.Equal(3, File.ReadAllLines(Path.Combine(_temp.Path, "transcript.jsonl")).Length);
    }

    [Fact]
    public async Task Session_without_audio_is_refused_and_its_transcript_left_alone()
    {
        WriteMeta(new LanguageMark(0, "ru"));
        File.WriteAllText(Path.Combine(_temp.Path, "transcript.jsonl"), "текст\n");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Retranscriber().RunAsync(_session, "ru", new Progress<double>(), CancellationToken.None));

        Assert.Equal("текст\n", File.ReadAllText(Path.Combine(_temp.Path, "transcript.jsonl")));
    }

    private SessionRetranscriber Retranscriber() =>
        new(_host, () => FakeSpeech.Detect, new ManualTimeProvider(Now), Logger.None);

    private void WriteChunks(int chunkSeconds, params byte[][] parts)
    {
        using var writer = new ChunkedWavWriter(_session.AudioFolder, PcmFormat.Mono16k, chunkSeconds);
        writer.Write([ .. parts.SelectMany(p => p) ]);
    }

    private void WriteMeta(params LanguageMark[] marks) =>
        new SessionMeta([ new SessionStart(Now, 0) ], "Mic", MetaFormat.From(PcmFormat.Mono16k), 2, "0.3.0-test", marks).WriteTo(_session);
}

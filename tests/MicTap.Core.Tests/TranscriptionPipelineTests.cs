using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Serilog.Core;

namespace MicTap.Core.Tests;

public sealed class TranscriptionPipelineTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempFolder _temp = new();
    private readonly FakeAudioSource _source = new();
    private readonly Recorder _recorder = new(Logger.None, new ManualTimeProvider(new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.FromHours(3))));
    private readonly List<SegmentTranscript> _lines = [];
    private TranscriberHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Speech_segments_become_lines_of_transcript_v2_and_silence_never_reaches_whisper()
    {
        var heard = new ConcurrentQueue<float[]>();
        var transcriber = FakeTranscriber.Saying((samples, language) =>
        {
            heard.Enqueue(samples);
            return $"{language}:{samples.Length / 16}ms";
        });
        using TranscriptionPipeline pipeline = Run(transcriber);
        SessionInfo session = _recorder.Start(Options("ru"), _source);

        Push(FakeSpeech.SilencePcm(1), FakeSpeech.SpeechPcm(2), FakeSpeech.SilencePcm(1), FakeSpeech.SpeechPcm(1.5), FakeSpeech.SilencePcm(10));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 2);

        Assert.Equal(
            [
                """{"schemaVersion":2,"seq":1,"start":1,"end":3,"lang":"ru","text":"ru:2000ms","edited":false}""",
                """{"schemaVersion":2,"seq":2,"start":4,"end":5.5,"lang":"ru","text":"ru:1500ms","edited":false}""",
            ],
            File.ReadAllLines(Path.Combine(session.Folder, "transcript.jsonl")));

        // Live drafts may ask too, but nothing Whisper gets is silence alone: ten seconds of it make no call.
        Assert.All(heard, samples => Assert.Contains(samples, sample => sample != 0));
    }

    [Fact]
    public async Task Language_mark_cuts_speech_and_each_part_is_recognized_in_its_language()
    {
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((_, language) => language == "ja" ? "日本語" : "русский"));
        _recorder.Start(Options("ru"), _source);

        Push(FakeSpeech.SpeechPcm(2));
        _recorder.MarkLanguage("ja");
        Push(FakeSpeech.SpeechPcm(2), FakeSpeech.SilencePcm(1));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 2);

        Assert.Equal([ (1, 0.0, 2.0, "ru", "русский"), (2, 2.0, 4.0, "ja", "日本語") ], Lines().Select(l => (l.Seq, l.Start, l.End, l.Lang, l.Text)));
    }

    [Fact]
    public async Task Segments_recorded_before_the_model_loads_wait_and_are_not_lost()
    {
        var loaded = new TaskCompletionSource<ITranscriber>();
        using TranscriptionPipeline pipeline = Run(_ => loaded.Task);
        _recorder.Start(Options("ru"), _source);

        Push(FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1), FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1));
        await _recorder.StopAsync();
        await Task.Delay(200);
        Assert.Empty(Lines());

        loaded.SetResult(FakeTranscriber.Saying((_, _) => "дождался"));

        await Eventually(() => Lines().Length == 2);
    }

    [Fact]
    public async Task Failed_transcriber_comes_back_and_the_segment_is_recognized_after_all()
    {
        var broken = new FakeTranscriber((_, _, _) => throw new InvalidOperationException("CUDA error"));
        var queue = new Queue<ITranscriber>([ broken, FakeTranscriber.Saying((_, _) => "после сбоя") ]);
        using TranscriptionPipeline pipeline = Run(_ => Task.FromResult(queue.Dequeue()));
        SessionInfo session = _recorder.Start(Options("ru"), _source);
        byte[] pcm = [ .. FakeSpeech.SpeechPcm(1), .. FakeSpeech.SilencePcm(1) ];

        Push(pcm);
        Assert.True(_recorder.IsRecording);
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 1);

        Assert.Equal("после сбоя", Lines()[ 0 ].Text);
        Assert.True(broken.Disposed);
        Assert.Equal(pcm, WavFiles.ReadAudio(WavFiles.Chunks(session.AudioFolder)));
    }

    [Fact]
    public async Task Stuck_transcriber_neither_blocks_capture_nor_loses_audio()
    {
        using TranscriptionPipeline pipeline = Run(new FakeTranscriber((_, _, cancel) => Task.Delay(Timeout.Infinite, cancel).ContinueWith(_ => "", TaskScheduler.Default)));
        SessionInfo session = _recorder.Start(Options("ru"), _source);
        byte[] pcm = [ .. FakeSpeech.SpeechPcm(1), .. FakeSpeech.SilencePcm(1), .. FakeSpeech.SpeechPcm(1) ];

        var capture = Stopwatch.StartNew();
        foreach (byte[] block in SyntheticPcm.Split(pcm, 3200))
        {
            _source.Push(block);
            Thread.Sleep(1); // let passes start and hang
        }

        capture.Stop();
        Assert.True(capture.Elapsed < TimeSpan.FromSeconds(2), $"capture blocked for {capture.Elapsed}");
        await _recorder.StopAsync();
        Assert.Equal(pcm, WavFiles.ReadAudio(WavFiles.Chunks(session.AudioFolder)));
    }

    [Fact]
    public async Task Live_text_shows_silence_without_calling_whisper_and_speech_with_it()
    {
        var transcriber = FakeTranscriber.Saying((_, language) => $"живой {language}");
        using TranscriptionPipeline pipeline = Run(transcriber);
        var live = new List<LiveTranscript>();
        pipeline.LiveTextChanged += (_, text) => { lock (live) live.Add(text); };
        _recorder.Start(Options("ru"), _source);

        Push(FakeSpeech.SilencePcm(3));
        await Eventually(() => { lock (live) return live.Any(l => l.Silence); });
        Assert.Equal(0, transcriber.Calls);

        Push(FakeSpeech.SpeechPcm(2));
        await Eventually(() => { lock (live) return live.Any(l => l.Text == "живой ru"); });
        await _recorder.StopAsync();
    }

    [Fact]
    public async Task Live_text_leaves_out_phrases_that_already_have_their_final_text()
    {
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((samples, _) => $"{samples.Length / 16}ms"));
        var live = new List<string>();
        pipeline.LiveTextChanged += (_, text) => { lock (live) live.Add(text.Text); };
        _recorder.Start(Options("ru"), _source);

        Push(FakeSpeech.SpeechPcm(2.5), FakeSpeech.SilencePcm(1), FakeSpeech.SpeechPcm(1));
        await Eventually(() => Lines().Length == 1);
        await Eventually(() => { lock (live) return live.Contains("2000ms"); }); // 1 s of silence and 1 s of speech after the phrase
        await _recorder.StopAsync();

        Assert.Equal("2500ms", Lines()[ 0 ].Text);
    }

    [Fact]
    public async Task Phrase_pause_of_the_project_joins_speech_into_one_line()
    {
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((samples, _) => $"{samples.Length / 16}ms"));
        _recorder.Start(Options("ru") with { PhrasePause = TimeSpan.FromSeconds(3) }, _source);

        Push(FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1), FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(4));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 1);

        Assert.Equal((0.0, 3.0, "3000ms"), (Lines()[ 0 ].Start, Lines()[ 0 ].End, Lines()[ 0 ].Text));
    }

    [Fact]
    public async Task Push_ends_the_phrase_being_spoken_and_comes_after_its_line()
    {
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((samples, _) => $"{samples.Length / 16}ms"));
        var linesAtPush = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.Pushed += (_, _) => linesAtPush.TrySetResult(Lines().Length);
        SessionInfo session = _recorder.Start(Options("ru") with { PhrasePause = TimeSpan.FromSeconds(3) }, _source);

        Push(FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1));
        pipeline.Push(session);

        Assert.Equal(1, await linesAtPush.Task.WaitAsync(Patience));
        Assert.Equal((0.0, 1.0, "1000ms"), (Lines()[ 0 ].Start, Lines()[ 0 ].End, Lines()[ 0 ].Text));
        Assert.True(_recorder.IsRecording);
        await _recorder.StopAsync();
    }

    [Fact]
    public async Task Recording_transcribed_comes_after_the_last_phrase_of_a_stopped_recording()
    {
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((_, _) => "фраза"));
        var linesAtEnd = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.RecordingTranscribed += (_, _) => linesAtEnd.TrySetResult(Lines().Length);
        _recorder.Start(Options("ru"), _source);

        // The second phrase is still being spoken at the stop: it becomes a segment only then.
        Push(FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1), FakeSpeech.SpeechPcm(1));
        await _recorder.StopAsync();

        Assert.Equal(2, await linesAtEnd.Task.WaitAsync(Patience));
    }

    [Fact]
    public async Task Recording_with_a_cloud_model_sends_its_phrases_there_and_keeps_the_draft_local()
    {
        var cloudModels = new ConcurrentQueue<string>();
        var cloud = FakeTranscriber.Saying((_, language) => $"облако {language}");
        var local = FakeTranscriber.Saying((_, _) => "локально");
        using TranscriptionPipeline pipeline = Run(local, (_, model) =>
        {
            cloudModels.Enqueue(model);
            return cloud;
        });
        _recorder.Start(Options("ru") with { CloudModel = "openai/whisper-large-v3" }, _source);

        Push(FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 1);

        Assert.Equal("облако ru", Lines()[ 0 ].Text);
        Assert.Equal([ "openai/whisper-large-v3" ], cloudModels);
        Assert.Equal(1, cloud.Calls);
    }

    [Fact]
    public async Task Cloud_failure_is_retried_and_reported_until_the_phrase_gets_its_text()
    {
        int attempts = 0;
        var problems = new ConcurrentQueue<string?>();
        var cloud = new FakeTranscriber((_, _, _) => Interlocked.Increment(ref attempts) == 1
            ? throw new CloudTranscriptionException("Router AI ответил 502: bad gateway")
            : Task.FromResult("со второго раза"));
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((_, _) => "локально"), (_, _) => cloud);
        pipeline.CloudProblem += (_, problem) => problems.Enqueue(problem);
        _recorder.Start(Options("ru") with { CloudModel = "m/x" }, _source);

        Push(FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 1);

        Assert.Equal("со второго раза", Lines()[ 0 ].Text);
        Assert.Collection(problems, first => Assert.Contains("502", first), recovered => Assert.Null(recovered));
    }

    [Fact]
    public async Task Segment_that_whisper_made_up_is_left_out_of_the_transcript()
    {
        // By length, not by call order: live passes ask Whisper too.
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((samples, _) =>
            samples.Length == FakeSpeech.Rate / 2 ? "Продолжение следует..." : "настоящая фраза"));
        SessionInfo session = _recorder.Start(Options("ru"), _source);

        Push(FakeSpeech.SpeechPcm(0.5), FakeSpeech.SilencePcm(1), FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 1);

        TranscriptLine line = Assert.Single(new JsonlTranscriptSink(session.Folder).Lines);
        Assert.Equal((1, "настоящая фраза"), (line.Seq, line.Text));
    }

    [Fact]
    public async Task Edit_through_the_sink_rewrites_the_line_by_seq()
    {
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((_, _) => "черновик"));
        SessionInfo session = _recorder.Start(Options("ru"), _source);
        Push(FakeSpeech.SpeechPcm(1));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 1);

        pipeline.SinkFor(session).Edit(Lines()[ 0 ].Seq, "исправлено");

        JsonElement line = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Path.Combine(session.Folder, "transcript.jsonl")))).RootElement;
        Assert.Equal("исправлено", line.GetProperty("text").GetString());
        Assert.True(line.GetProperty("edited").GetBoolean());
    }

    [Fact]
    public async Task Appended_recording_continues_seq_and_session_time_in_its_language()
    {
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((_, language) => language));
        SessionInfo session = _recorder.Start(Options("ru"), _source);
        Push(FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 1);

        var again = new FakeAudioSource();
        _recorder.Start(Options("ja"), again);
        PushTo(again, FakeSpeech.SilencePcm(0.5), FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 2);

        Assert.Equal(
            [ (1, 0.0, 1.0, "ru"), (2, 2.5, 3.5, "ja") ],
            new JsonlTranscriptSink(session.Folder).Lines.Select(l => (l.Seq, l.Start, l.End, l.Lang)));
    }

    [Fact]
    public async Task Delete_after_transcript_removes_a_chunk_once_its_speech_is_written()
    {
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((_, _) => "фраза"));
        SessionInfo session = _recorder.Start(Options("ru", AudioRetention.DeleteAfterTranscript, chunkSeconds: 1), _source);
        string first = Path.Combine(session.AudioFolder, "000.wav");

        Push(FakeSpeech.SpeechPcm(1.5), FakeSpeech.SilencePcm(1.5));
        await Eventually(() => Lines().Length == 1 && !File.Exists(first));
        Assert.True(_recorder.IsRecording);

        Push(FakeSpeech.SpeechPcm(1), FakeSpeech.SilencePcm(1));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 2 && !Directory.EnumerateFiles(session.AudioFolder).Any());

        Assert.Equal(2, File.ReadAllLines(Path.Combine(session.Folder, "transcript.jsonl")).Length);
    }

    [Fact]
    public async Task Keep_leaves_every_chunk_after_recognition()
    {
        using TranscriptionPipeline pipeline = Run(FakeTranscriber.Saying((_, _) => "фраза"));
        SessionInfo session = _recorder.Start(Options("ru", AudioRetention.Keep, chunkSeconds: 1), _source);

        Push(FakeSpeech.SpeechPcm(1.5), FakeSpeech.SilencePcm(1.5));
        await _recorder.StopAsync();
        await Eventually(() => Lines().Length == 1);

        Assert.Equal(3, Directory.EnumerateFiles(session.AudioFolder).Count());
    }

    private TranscriptionPipeline Run(FakeTranscriber transcriber, Func<SessionInfo, string, ITranscriber>? cloud = null) =>
        Run(_ => Task.FromResult<ITranscriber>(transcriber), cloud);

    private TranscriptionPipeline Run(Func<CancellationToken, Task<ITranscriber>> load, Func<SessionInfo, string, ITranscriber>? cloud = null)
    {
        _host = new TranscriberHost(load, Logger.None, TimeProvider.System, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(80));
        var pipeline = new TranscriptionPipeline(
            _host, Task.FromResult<Func<SpeechDetector>>(() => FakeSpeech.Detect), Logger.None, TimeSpan.FromMilliseconds(50), cloud);
        pipeline.SegmentTranscribed += (_, line) => { lock (_lines) _lines.Add(line); };
        pipeline.Attach(_recorder);
        _host.Start();
        pipeline.Start();
        return pipeline;
    }

    private TranscriptLine[] Lines()
    {
        lock (_lines)
        {
            return [ .. _lines.Select(l => l.Line) ];
        }
    }

    private void Push(params byte[][] parts) => PushTo(_source, parts);

    private static void PushTo(FakeAudioSource source, params byte[][] parts)
    {
        foreach (byte[] block in SyntheticPcm.Split([ .. parts.SelectMany(p => p) ], 3200))
        {
            source.Push(block);
        }
    }

    private RecordingOptions Options(string language, AudioRetention retention = AudioRetention.Keep, int chunkSeconds = 60) =>
        new(SessionFolders.Open(_temp.Path, "Пара"), "Fake microphone", chunkSeconds, "0.4.0-test", language, retention);

    private static async Task Eventually(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, "condition not met in time");
            await Task.Delay(20);
        }
    }
}

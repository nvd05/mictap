using System.Diagnostics;
using System.Text.Json;
using Serilog.Core;

namespace MicTap.Core.Tests;

public sealed class RecorderTests : IDisposable
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 26, 14, 5, 30, TimeSpan.FromHours(3));
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempFolder _temp = new();
    private readonly ManualTimeProvider _time = new(StartedAt);
    private readonly FakeAudioSource _source = new();
    private readonly Recorder _recorder;
    private readonly List<RecordingStopped> _stops = [];
    private readonly TaskCompletionSource<RecordingStopped> _firstStop = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecorderTests()
    {
        _recorder = Watch(new Recorder(Logger.None, _time));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Session_is_recorded_into_rotated_chunks_with_meta()
    {
        SessionInfo session = Start(chunkSeconds: 1);
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(2.5));

        foreach (byte[] block in SyntheticPcm.Split(pcm, 3200))
        {
            _source.Push(block);
        }

        await _recorder.StopAsync();

        Assert.Equal("2026-10-26_14-05-30", session.Name);
        Assert.True(_source.Started);
        Assert.True(_source.Disposed);
        Assert.False(_recorder.IsRecording);
        RecordingStopped stopped = Assert.Single(Stops());
        Assert.Equal(StopReason.Requested, stopped.Reason);
        Assert.Null(stopped.Error);
        Assert.Equal(TimeSpan.FromSeconds(2.5), stopped.Recorded);

        string[] chunks = WavFiles.Chunks(session.AudioFolder);
        Assert.Equal(["000.wav", "001.wav", "002.wav"], chunks.Select(Path.GetFileName));
        Assert.Equal(pcm, WavFiles.ReadAudio(chunks));
        Assert.Equal(["audio", "meta.json"], Directory.EnumerateFileSystemEntries(session.Folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Meta_json_describes_the_session()
    {
        SessionInfo session = Start(chunkSeconds: 1);
        await _recorder.StopAsync();

        using var meta = JsonDocument.Parse(File.ReadAllText(session.MetaFile));
        JsonElement root = meta.RootElement;
        Assert.Equal(
            ["schemaVersion", "starts", "device", "format", "chunkSeconds", "appVersion", "languageMarks"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        JsonElement start = Assert.Single(root.GetProperty("starts").EnumerateArray());
        DateTimeOffset started = start.GetProperty("time").GetDateTimeOffset();
        Assert.Equal(StartedAt, started);
        Assert.Equal(StartedAt.Offset, started.Offset);
        Assert.Equal(0, start.GetProperty("offset").GetDouble());
        JsonElement mark = Assert.Single(root.GetProperty("languageMarks").EnumerateArray());
        Assert.Equal(0, mark.GetProperty("at").GetDouble());
        Assert.Equal("ru", mark.GetProperty("lang").GetString());
        Assert.Equal("Fake microphone", root.GetProperty("device").GetString());
        JsonElement format = root.GetProperty("format");
        Assert.Equal("pcm_s16le", format.GetProperty("encoding").GetString());
        Assert.Equal(16000, format.GetProperty("sampleRate").GetInt32());
        Assert.Equal(1, format.GetProperty("channels").GetInt32());
        Assert.Equal(16, format.GetProperty("bitsPerSample").GetInt32());
        Assert.Equal(1, root.GetProperty("chunkSeconds").GetInt32());
        Assert.Equal("1.0.0-test", root.GetProperty("appVersion").GetString());
    }

    [Fact]
    public async Task Language_mark_is_stored_at_the_audio_captured_so_far()
    {
        var marks = new List<LanguageMarked>();
        _recorder.LanguageMarked += (_, marked) => marks.Add(marked);
        SessionInfo session = Start(chunkSeconds: 60);
        foreach (byte[] block in SyntheticPcm.Split(SyntheticPcm.Create(TimeSpan.FromSeconds(1.5)), 3200))
        {
            _source.Push(block);
        }

        LanguageMark? mark = _recorder.MarkLanguage("ja");
        await _recorder.StopAsync();

        Assert.Equal(new LanguageMark(1.5, "ja"), mark);
        Assert.Equal([new LanguageMark(0, "ru"), new LanguageMark(1.5, "ja")], marks.Select(m => m.Mark));
        Assert.All(marks, m => Assert.Equal(session, m.Session));
        Assert.Equal(
            [new LanguageMark(0, "ru"), new LanguageMark(1.5, "ja")],
            SessionMeta.Read(session, languageOfSchema1: "ru").LanguageMarks);
    }

    [Fact]
    public void Language_mark_without_a_recording_is_ignored()
    {
        Assert.Null(_recorder.MarkLanguage("ja"));
    }

    [Fact]
    public void Meta_json_of_schema_1_reads_as_one_start_in_the_given_language()
    {
        var session = new SessionInfo("2026-10-26_1405", _temp.Path);
        File.WriteAllText(session.MetaFile, """
            {"schemaVersion":1,"started":"2026-10-26T14:05:30+03:00","device":"Mic",
             "format":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1,"bitsPerSample":16},
             "chunkSeconds":60,"appVersion":"0.2.1"}
            """);

        SessionMeta meta = SessionMeta.Read(session, languageOfSchema1: "ja");

        Assert.Equal([new SessionStart(StartedAt, 0)], meta.Starts);
        Assert.Equal([new LanguageMark(0, "ja")], meta.LanguageMarks);
        Assert.Equal(60, meta.ChunkSeconds);
    }

    [Fact]
    public async Task Writer_puts_audio_on_disk_every_second()
    {
        var written = 0;
        _recorder.ProgressChanged += (_, _) => Interlocked.Increment(ref written);
        SessionInfo session = Start(chunkSeconds: 60);
        string chunk = Path.Combine(session.AudioFolder, "000.wav");

        // Small blocks stay in the file buffer until a flush.
        foreach (byte[] block in SyntheticPcm.Split(SyntheticPcm.Create(TimeSpan.FromSeconds(0.1)), 320))
        {
            _source.Push(block);
        }

        await Eventually(() => Volatile.Read(ref written) == 10);
        Assert.Equal(WavHeader.Size, WavFiles.LengthOnDisk(chunk));

        _time.Advance(TimeSpan.FromSeconds(1));

        await Eventually(() => WavFiles.LengthOnDisk(chunk) == WavHeader.Size + 3200);
        Assert.Equal((0u, 0u), WavFiles.HeaderSizes(chunk));
        Assert.True(_recorder.IsRecording);
        await _recorder.StopAsync();
    }

    [Fact]
    public async Task Slow_disk_neither_blocks_capture_nor_loses_audio()
    {
        using var disk = new ManualResetEventSlim(false);
        _recorder.BeforeDiskWrite = disk.Wait;
        SessionInfo session = Start(chunkSeconds: 1);
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(3));

        // The writer is held while 3 s of audio arrive: capture must not wait for it.
        var capture = Stopwatch.StartNew();
        foreach (byte[] block in SyntheticPcm.Split(pcm, 3200))
        {
            _source.Push(block);
        }

        capture.Stop();
        Assert.True(capture.Elapsed < TimeSpan.FromSeconds(1), $"capture blocked for {capture.Elapsed}");
        Assert.True(_recorder.IsRecording);

        disk.Set();
        await _recorder.StopAsync();

        Assert.Equal(StopReason.Requested, Assert.Single(Stops()).Reason);
        Assert.Equal(pcm, WavFiles.ReadAudio(WavFiles.Chunks(session.AudioFolder)));
    }

    [Fact]
    public async Task Disk_stalled_beyond_the_write_buffer_stops_recording_and_keeps_queued_audio()
    {
        Recorder recorder = Watch(new Recorder(Logger.None, _time, writeBuffer: TimeSpan.FromSeconds(1)));
        using var disk = new ManualResetEventSlim(false);
        recorder.BeforeDiskWrite = disk.Wait;
        SessionInfo session = recorder.Start(Options(chunkSeconds: 60), _source);
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(1.5));

        foreach (byte[] block in SyntheticPcm.Split(pcm, 3200))
        {
            _source.Push(block);
        }

        Assert.True(_source.StopRequested);
        disk.Set();
        RecordingStopped stopped = await _firstStop.Task.WaitAsync(Patience);

        Assert.Equal(StopReason.WriteStalled, stopped.Reason);
        Assert.Equal(TimeSpan.FromSeconds(1), stopped.Recorded);
        Assert.Equal(pcm[..32000], WavFiles.ReadAudio(Path.Combine(session.AudioFolder, "000.wav")));
    }

    [Fact]
    public async Task Device_failure_stops_recording_and_keeps_audio()
    {
        SessionInfo session = Start(chunkSeconds: 1);
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(1.5));
        foreach (byte[] block in SyntheticPcm.Split(pcm, 3200))
        {
            _source.Push(block);
        }

        var error = new InvalidOperationException("NoDriver calling waveInAddBuffer");
        _source.Fail(error);
        RecordingStopped stopped = await _firstStop.Task.WaitAsync(Patience);

        Assert.False(_recorder.IsRecording);
        Assert.Equal(StopReason.DeviceFailed, stopped.Reason);
        Assert.Same(error, stopped.Error);
        Assert.Equal(TimeSpan.FromSeconds(1.5), stopped.Recorded);
        Assert.True(_source.Disposed);
        Assert.Equal(pcm, WavFiles.ReadAudio(WavFiles.Chunks(session.AudioFolder)));
    }

    [Fact]
    public async Task Silent_device_stops_recording_after_timeout()
    {
        SessionInfo session = Start(chunkSeconds: 60);
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(0.2));
        _source.Push(pcm);

        _time.Advance(TimeSpan.FromSeconds(4.9));
        _recorder.Tick();
        Assert.False(_firstStop.Task.IsCompleted);
        Assert.False(_source.StopRequested);

        _time.Advance(TimeSpan.FromSeconds(0.1));
        _recorder.Tick();
        RecordingStopped stopped = await _firstStop.Task.WaitAsync(Patience);

        Assert.False(_recorder.IsRecording);
        Assert.Equal(StopReason.DeviceSilent, stopped.Reason);
        Assert.True(_source.StopRequested);
        Assert.True(_source.Disposed);
        Assert.Equal(pcm, WavFiles.ReadAudio(Path.Combine(session.AudioFolder, "000.wav")));
    }

    [Fact]
    public async Task Level_and_progress_follow_the_audio()
    {
        var levels = new List<double>();
        var progress = new List<RecordingProgress>();
        _recorder.LevelChanged += (_, level) => levels.Add(level);
        _recorder.ProgressChanged += (_, p) =>
        {
            lock (progress)
            {
                progress.Add(p);
            }
        };
        Start(chunkSeconds: 1);

        _source.Push(new byte[3200]);
        _source.Push([.. Enumerable.Repeat<byte>(0, 3198), 0x00, 0x40]); // one sample at 16384 = half scale
        _source.Push(new byte[32000]);
        await _recorder.StopAsync();

        Assert.Equal([0.0, 0.5, 0.0], levels);
        Assert.Equal([0, 0, 1], progress.Select(p => p.ChunkIndex));
        Assert.Equal(TimeSpan.FromSeconds(1.2), progress[^1].Recorded);
    }

    [Fact]
    public async Task Second_start_is_rejected_while_recording()
    {
        Start(chunkSeconds: 60);
        var second = new FakeAudioSource();

        Assert.Throws<InvalidOperationException>(() => _recorder.Start(Options(60), second));

        Assert.False(second.Started);
        Assert.True(second.Disposed);
        Assert.True(_recorder.IsRecording);
        await _recorder.StopAsync();
    }

    [Fact]
    public async Task Next_session_in_the_same_second_gets_its_own_folder()
    {
        SessionInfo first = Start(chunkSeconds: 60);
        await _recorder.StopAsync();

        SessionInfo second = _recorder.Start(Options(60), new FakeAudioSource());
        await _recorder.StopAsync();

        Assert.Equal("2026-10-26_14-05-30", first.Name);
        Assert.Equal("2026-10-26_14-05-30_2", second.Name);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < Patience, "condition not met in time");
            await Task.Delay(20);
        }
    }

    private Recorder Watch(Recorder recorder)
    {
        recorder.Stopped += (_, stopped) =>
        {
            lock (_stops)
            {
                _stops.Add(stopped);
            }

            _firstStop.TrySetResult(stopped);
        };
        return recorder;
    }

    private RecordingStopped[] Stops()
    {
        lock (_stops)
        {
            return [.. _stops];
        }
    }

    private SessionInfo Start(int chunkSeconds) => _recorder.Start(Options(chunkSeconds), _source);

    private RecordingOptions Options(int chunkSeconds) =>
        new(_temp.Path, "Fake microphone", chunkSeconds, "1.0.0-test", "ru");
}

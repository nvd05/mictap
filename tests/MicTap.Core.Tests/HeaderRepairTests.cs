using System.Buffers.Binary;

namespace MicTap.Core.Tests;

public sealed class HeaderRepairTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Open_chunk_gets_sizes_from_file_length()
    {
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(0.75));
        string chunk = WriteOpenChunk("000.wav", pcm);

        Assert.Equal(WavRepairResult.Repaired, WavHeader.RepairIfOpen(chunk));

        Assert.Equal(((uint)pcm.Length + 36, (uint)pcm.Length), WavFiles.HeaderSizes(chunk));
        Assert.Equal(pcm, WavFiles.ReadAudio(chunk));
    }

    [Fact]
    public void Chunk_with_riff_size_but_no_data_size_is_repaired()
    {
        // A crash between the two size writes of a normal close.
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(0.5));
        string chunk = WriteOpenChunk("000.wav", pcm);
        byte[] bytes = File.ReadAllBytes(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)pcm.Length + 36);
        File.WriteAllBytes(chunk, bytes);

        Assert.Equal(WavRepairResult.Repaired, WavHeader.RepairIfOpen(chunk));

        Assert.Equal(pcm, WavFiles.ReadAudio(chunk));
    }

    [Fact]
    public void Trailing_partial_sample_is_cut_off()
    {
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(0.5));
        string chunk = WriteOpenChunk("000.wav", [.. pcm, 0x7F]);

        Assert.Equal(WavRepairResult.Repaired, WavHeader.RepairIfOpen(chunk));

        Assert.Equal(WavHeader.Size + pcm.Length, new FileInfo(chunk).Length);
        Assert.Equal(pcm, WavFiles.ReadAudio(chunk));
    }

    [Fact]
    public void Open_chunk_without_audio_becomes_an_empty_wav()
    {
        string chunk = WriteOpenChunk("000.wav", []);

        Assert.Equal(WavRepairResult.Repaired, WavHeader.RepairIfOpen(chunk));

        Assert.Equal((36u, 0u), WavFiles.HeaderSizes(chunk));
        Assert.Empty(WavFiles.ReadAudio(chunk));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Closed_chunk_is_left_byte_for_byte(double seconds)
    {
        using (var writer = new ChunkedWavWriter(_temp.Path, PcmFormat.Mono16k, chunkSeconds: 60))
        {
            writer.Write(SyntheticPcm.Create(TimeSpan.FromSeconds(seconds)));
        }

        string chunk = Path.Combine(_temp.Path, "000.wav");
        byte[] before = File.ReadAllBytes(chunk);

        Assert.Equal(WavRepairResult.Intact, WavHeader.RepairIfOpen(chunk));

        Assert.Equal(before, File.ReadAllBytes(chunk));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void Foreign_file_is_not_touched(int length)
    {
        byte[] bytes = Enumerable.Range(0, length).Select(n => (byte)n).ToArray();
        string path = Path.Combine(_temp.Path, "foreign.wav");
        File.WriteAllBytes(path, bytes);

        Assert.Equal(WavRepairResult.NotRecognized, WavHeader.RepairIfOpen(path));

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Only_the_latest_session_of_each_project_is_repaired()
    {
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(0.25));
        Projects.Create(_temp.Path, new ProjectSettings("Матан", ["ru"], AudioRetention.Keep));
        Projects.Create(_temp.Path, new ProjectSettings("Японский", ["ja"], AudioRetention.Keep));
        string older = WriteOpenChunk(@"Матан\старая\audio\000.wav", pcm);
        string latestClosed = WriteClosedChunk(@"Матан\новая\audio", pcm);
        string latestOpen = WriteOpenChunk(@"Матан\новая\audio\001.wav", pcm);
        string otherProject = WriteOpenChunk(@"Японский\урок\audio\000.wav", pcm);
        string foreign = WriteOpenChunk(@"не проект\сессия\audio\000.wav", pcm);
        MarkRecorded(@"Матан\старая", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        MarkRecorded(@"Матан\новая", new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        MarkRecorded(@"Японский\урок", new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        MarkRecorded(@"не проект\сессия", new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));

        IReadOnlyList<RepairReport> reports = SessionRepair.RepairProjects(_temp.Path);

        Assert.Equal(["новая", "урок"], reports.Select(r => r.Session.Name));
        Assert.Equal([latestOpen], reports[0].Repaired);
        Assert.Equal([otherProject], reports[1].Repaired);
        Assert.Equal(pcm, WavFiles.ReadAudio(latestOpen));
        Assert.Equal(pcm, WavFiles.ReadAudio(latestClosed));
        Assert.Equal((0u, 0u), WavFiles.HeaderSizes(older));
        Assert.Equal((0u, 0u), WavFiles.HeaderSizes(foreign));
    }

    [Fact]
    public void Project_without_sessions_reports_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "notes"));

        Assert.Null(SessionRepair.RepairLatest(_temp.Path));
        Assert.Null(SessionRepair.RepairLatest(Path.Combine(_temp.Path, "absent")));
        Assert.Empty(SessionRepair.RepairProjects(Path.Combine(_temp.Path, "absent")));
    }

    private string WriteOpenChunk(string relativePath, byte[] pcm)
    {
        string path = Path.Combine(_temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. WavHeader.CreateOpen(PcmFormat.Mono16k), .. pcm]);
        return path;
    }

    private string WriteClosedChunk(string relativeFolder, byte[] pcm)
    {
        string folder = Path.Combine(_temp.Path, relativeFolder);
        using (var writer = new ChunkedWavWriter(folder, PcmFormat.Mono16k, chunkSeconds: 60))
        {
            writer.Write(pcm);
        }

        return Path.Combine(folder, "000.wav");
    }

    /// <summary>Sessions are ordered by the time their meta.json was last written.</summary>
    private void MarkRecorded(string relativeSession, DateTime lastWrite)
    {
        string meta = Path.Combine(_temp.Path, relativeSession, "meta.json");
        File.WriteAllText(meta, "{}");
        File.SetLastWriteTimeUtc(meta, lastWrite);
    }
}

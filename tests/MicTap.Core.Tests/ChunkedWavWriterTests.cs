namespace MicTap.Core.Tests;

public sealed class ChunkedWavWriterTests : IDisposable
{
    private const int OneSecond = 32000;

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Stream_is_split_into_exact_chunks_without_gaps()
    {
        byte[] pcm = SyntheticPcm.Create(TimeSpan.FromSeconds(3.5));

        using (var writer = new ChunkedWavWriter(_temp.Path, PcmFormat.Mono16k, chunkSeconds: 1))
        {
            // One write spanning two boundaries, then uneven pieces that straddle the next one.
            writer.Write(pcm.AsSpan(0, 70000));
            foreach (byte[] piece in SyntheticPcm.Split(pcm[70000..], 3200, 2468, 6, 4000))
            {
                writer.Write(piece);
            }

            Assert.Equal(pcm.Length, writer.TotalBytes);
        }

        string[] chunks = WavFiles.Chunks(_temp.Path);
        Assert.Equal([ "000.wav", "001.wav", "002.wav", "003.wav" ], chunks.Select(Path.GetFileName));
        Assert.Equal(
            [ OneSecond, OneSecond, OneSecond, OneSecond / 2 ],
            chunks.Select(chunk => WavFiles.ReadAudio(chunk).Length));
        Assert.Equal(pcm, WavFiles.ReadAudio(chunks));
    }

    [Fact]
    public void Next_chunk_is_opened_before_previous_is_closed()
    {
        var closes = new List<(int Index, int CurrentIndex, bool NextExists)>();
        var writer = new ChunkedWavWriter(_temp.Path, PcmFormat.Mono16k, chunkSeconds: 1);
        writer.ChunkClosed += (_, chunk) => closes.Add(
            (chunk.Index, writer.CurrentIndex, File.Exists(Path.Combine(_temp.Path, ChunkedWavWriter.FileName(chunk.Index + 1)))));

        foreach (byte[] piece in SyntheticPcm.Split(SyntheticPcm.Create(TimeSpan.FromSeconds(2.5)), 3200))
        {
            writer.Write(piece);
        }

        writer.Dispose();

        Assert.Equal([ (0, 1, true), (1, 2, true), (2, 2, false) ], closes);
    }

    [Fact]
    public void Open_chunk_keeps_zero_sizes_and_flush_puts_audio_on_disk()
    {
        using var writer = new ChunkedWavWriter(_temp.Path, PcmFormat.Mono16k, chunkSeconds: 60);
        string chunk = Path.Combine(_temp.Path, "000.wav");
        Assert.Equal(WavHeader.Size, WavFiles.LengthOnDisk(chunk));

        foreach (byte[] piece in SyntheticPcm.Split(SyntheticPcm.Create(TimeSpan.FromSeconds(0.1)), 320))
        {
            writer.Write(piece);
        }

        writer.Flush();

        Assert.Equal(WavHeader.Size + 3200, WavFiles.LengthOnDisk(chunk));
        Assert.Equal((0u, 0u), WavFiles.HeaderSizes(chunk));
    }

    [Fact]
    public void Stream_ending_on_a_boundary_leaves_no_empty_chunk()
    {
        using (var writer = new ChunkedWavWriter(_temp.Path, PcmFormat.Mono16k, chunkSeconds: 1))
        {
            writer.Write(SyntheticPcm.Create(TimeSpan.FromSeconds(2)));
        }

        Assert.Equal([ "000.wav", "001.wav" ], WavFiles.Chunks(_temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Existing_chunk_is_never_overwritten()
    {
        string chunk = Path.Combine(_temp.Path, "000.wav");
        File.WriteAllBytes(chunk, [ 1, 2, 3 ]);

        Assert.Throws<IOException>(() => new ChunkedWavWriter(_temp.Path, PcmFormat.Mono16k, chunkSeconds: 60));
        Assert.Equal([ 1, 2, 3 ], File.ReadAllBytes(chunk));
    }
}

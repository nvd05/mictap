using System.Threading.Channels;

namespace MicTap.Core;

/// <summary>
/// Disk stage of the pipeline: drains captured audio from a channel on its own thread, writes it through
/// <see cref="ChunkedWavWriter"/> and pushes it to disk every <see cref="FlushInterval"/>.
/// The capture thread only queues blocks and never waits for the disk.
/// </summary>
internal sealed class ChunkWriterStage
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(200);

    private readonly ChunkedWavWriter _writer;
    private readonly ChannelReader<byte[]> _input;
    private readonly TimeProvider _time;
    private readonly Action? _beforeWrite;
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChunkWriterStage(ChunkedWavWriter writer, ChannelReader<byte[]> input, TimeProvider time, Action? beforeWrite)
    {
        _writer = writer;
        _input = input;
        _time = time;
        _beforeWrite = beforeWrite;
    }

    /// <summary>Raised on the writer thread after a block reached the chunk file; the argument is its size.</summary>
    public event Action<int>? Written;

    /// <summary>Completes once the input is drained and the last chunk is closed; carries the write error, if any.</summary>
    public Task<Exception?> Completion => _completion.Task;

    public void Start() => new Thread(Run) { IsBackground = true, Name = "mictap disk writer" }.Start();

    private void Run()
    {
        Exception? failure = null;
        try
        {
            Drain();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex;
        }

        try
        {
            _writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The open chunk keeps zero sizes in its header and gets repaired on the next start.
            failure ??= ex;
        }

        _completion.SetResult(failure);
    }

    private void Drain()
    {
        long lastFlush = _time.GetTimestamp();
        Task<bool>? next = null;
        while (true)
        {
            while (_input.TryRead(out byte[]? block))
            {
                _beforeWrite?.Invoke();
                _writer.Write(block);
                Written?.Invoke(block.Length);
            }

            if (_time.GetElapsedTime(lastFlush) >= FlushInterval)
            {
                _writer.Flush();
                lastFlush = _time.GetTimestamp();
            }

            next ??= _input.WaitToReadAsync().AsTask();
            if (!next.Wait(IdlePoll))
            {
                continue; // nothing new: come round again so the flush stays on time
            }

            if (!next.Result)
            {
                return; // input completed and fully drained
            }

            next = null;
        }
    }
}

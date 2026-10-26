using Serilog.Core;

namespace MicTap.Core.Tests;

public sealed class TranscriberHostTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Passes_wait_while_the_transcriber_loads()
    {
        var loaded = new TaskCompletionSource<ITranscriber>();
        using var host = new TranscriberHost(_ => loaded.Task, Logger.None, TimeProvider.System);
        host.Start();

        Task<IReadOnlyList<TranscriptSegment>> pass = host.TranscribeAsync([ 0f ], "ru", CancellationToken.None);
        await Task.Delay(100);
        Assert.False(pass.IsCompleted);

        loaded.SetResult(FakeTranscriber.Saying((_, _) => "готово"));

        Assert.Equal("готово", Assert.Single(await pass.WaitAsync(Patience)).Text);
        Assert.Equal(TranscriberStatus.Ready, host.State.Status);
        Assert.Equal("fake", host.State.Runtime);
    }

    [Fact]
    public async Task Failed_transcriber_is_replaced_by_a_fresh_one_and_disposed()
    {
        var broken = new FakeTranscriber((_, _, _) => throw new InvalidOperationException("CUDA error"));
        var healthy = FakeTranscriber.Saying((_, _) => "снова работает");
        var queue = new Queue<ITranscriber>([ broken, healthy ]);
        var states = new List<TranscriberState>();
        using var host = Host(_ => Task.FromResult(queue.Dequeue()));
        host.StateChanged += (_, state) => { lock (states) states.Add(state); };
        host.Start();

        var error = await Assert.ThrowsAsync<TranscriberFailedException>(() => host.TranscribeAsync([ 0f ], "ru", CancellationToken.None));
        IReadOnlyList<TranscriptSegment> after = await host.TranscribeAsync([ 0f ], "ru", CancellationToken.None).WaitAsync(Patience);

        Assert.Equal("CUDA error", error.InnerException!.Message);
        Assert.Equal("снова работает", Assert.Single(after).Text);
        Assert.True(broken.Disposed);
        Assert.False(healthy.Disposed);
        lock (states)
        {
            Assert.Contains(states, s => s.Status == TranscriberStatus.Failed && s.Error?.Message == "CUDA error");
        }
    }

    [Fact]
    public async Task Retries_back_off_doubling_up_to_the_limit()
    {
        var retries = new List<TimeSpan>();
        var attempts = 0;
        using var host = Host(_ => ++attempts <= 5
            ? Task.FromException<ITranscriber>(new FileNotFoundException("model missing"))
            : Task.FromResult<ITranscriber>(FakeTranscriber.Saying((_, _) => "")));
        host.StateChanged += (_, state) =>
        {
            if (state.Status == TranscriberStatus.Failed)
            {
                lock (retries) retries.Add(state.RetryIn);
            }
        };
        host.Start();

        await host.TranscribeAsync([ 0f ], "ru", CancellationToken.None).WaitAsync(Patience);

        lock (retries)
        {
            Assert.Equal([ 10, 20, 40, 80, 80 ], retries.Select(r => r.TotalMilliseconds));
        }
    }

    [Fact]
    public async Task Discarded_transcriber_is_disposed_only_after_its_running_pass_returns()
    {
        var slowPass = new TaskCompletionSource<string>();
        var calls = 0;
        var shared = new FakeTranscriber((_, _, _) => Interlocked.Increment(ref calls) == 1
            ? slowPass.Task
            : throw new InvalidOperationException("second pass fails"));
        var queue = new Queue<ITranscriber>([ shared, FakeTranscriber.Saying((_, _) => "") ]);
        using var host = Host(_ => Task.FromResult(queue.Dequeue()));
        host.Start();

        Task<IReadOnlyList<TranscriptSegment>> running = host.TranscribeAsync([ 0f ], "ru", CancellationToken.None);
        await Assert.ThrowsAsync<TranscriberFailedException>(() => host.TranscribeAsync([ 0f ], "ru", CancellationToken.None));
        Assert.False(shared.Disposed);

        slowPass.SetResult("дошёл");

        Assert.Equal("дошёл", Assert.Single(await running.WaitAsync(Patience)).Text);
        Assert.True(shared.Disposed);
    }

    [Fact]
    public async Task Injected_failure_fails_one_pass_and_the_host_recovers()
    {
        var loads = 0;
        using var host = Host(_ =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult<ITranscriber>(FakeTranscriber.Saying((_, _) => "текст"));
        });
        host.Start();
        await host.TranscribeAsync([ 0f ], "ru", CancellationToken.None).WaitAsync(Patience);

        host.InjectFailure();

        await Assert.ThrowsAsync<TranscriberFailedException>(() => host.TranscribeAsync([ 0f ], "ru", CancellationToken.None));
        Assert.Equal("текст", Assert.Single(await host.TranscribeAsync([ 0f ], "ru", CancellationToken.None).WaitAsync(Patience)).Text);
        Assert.Equal(2, loads);
    }

    private static TranscriberHost Host(Func<CancellationToken, Task<ITranscriber>> load) =>
        new(load, Logger.None, TimeProvider.System, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(80));
}

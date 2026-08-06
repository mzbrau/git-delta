using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class RepositoryGateTests
{
    [Test]
    public async Task Concurrent_Reads_Run_Together()
    {
        var gate = new RepositoryGate();
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = gate.RunReadAsync(async _ =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
            return 1;
        }, CancellationToken.None);

        var second = gate.RunReadAsync(async _ =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
            return 2;
        }, CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref started) == 2, TimeSpan.FromSeconds(2));
        release.SetResult();

        Assert.That(await first, Is.EqualTo(1));
        Assert.That(await second, Is.EqualTo(2));
    }

    [Test]
    public async Task WorktreeWrite_Blocks_Overlapping_Read()
    {
        var gate = new RepositoryGate();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var write = gate.RunWorktreeWriteAsync(async _ =>
        {
            writeEntered.SetResult();
            await releaseWrite.Task;
            return 0;
        }, CancellationToken.None);

        await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var read = gate.RunReadAsync(_ =>
        {
            readEntered.TrySetResult();
            return Task.FromResult(1);
        }, CancellationToken.None);

        var winner = await Task.WhenAny(readEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.That(winner, Is.Not.EqualTo(readEntered.Task), "Read should remain blocked while worktree write is held.");
        Assert.That(readEntered.Task.IsCompleted, Is.False);

        releaseWrite.SetResult();
        await write;
        Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(1));
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(gate.CurrentEpoch, Is.EqualTo(1));
    }

    [Test]
    public async Task IndexWrite_Does_Not_Block_Read()
    {
        var gate = new RepositoryGate();
        var indexEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIndex = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readEntered = 0;

        var indexWrite = gate.RunIndexWriteAsync(async _ =>
        {
            indexEntered.SetResult();
            await releaseIndex.Task;
            return 0;
        }, CancellationToken.None);

        await indexEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var read = gate.RunReadAsync(_ =>
        {
            Interlocked.Exchange(ref readEntered, 1);
            return Task.FromResult(42);
        }, CancellationToken.None);

        Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(42));
        Assert.That(Volatile.Read(ref readEntered), Is.EqualTo(1));

        releaseIndex.SetResult();
        await indexWrite;
        Assert.That(gate.CurrentEpoch, Is.EqualTo(1));
    }

    [Test]
    public async Task Network_Does_Not_Block_Read()
    {
        var gate = new RepositoryGate();
        var networkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNetwork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var network = gate.RunNetworkAsync(async _ =>
        {
            networkEntered.SetResult();
            await releaseNetwork.Task;
        }, CancellationToken.None);

        await networkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var value = await gate.RunReadAsync(_ => Task.FromResult(7), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(value, Is.EqualTo(7));

        releaseNetwork.SetResult();
        await network;
        Assert.That(gate.CurrentEpoch, Is.EqualTo(0));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not met before timeout.");
            await Task.Delay(10);
        }
    }
}

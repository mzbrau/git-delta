using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Git;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class RepositoryGateProviderTests
{
    [Test]
    public async Task ForAsync_Does_Not_Block_Caller_While_RevParse_Is_Slow()
    {
        var gitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = Substitute.For<IGitProcessRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<GitProcessOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                gitEntered.TrySetResult();
                await releaseGit.Task.ConfigureAwait(false);
                return new GitCommandResult(0, "/tmp/repo/.git\n", "", false, false);
            });

        var provider = new RepositoryGateProvider(runner);
        var previous = SynchronizationContext.Current;
        var sync = new ThreadPoolSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(sync);
        try
        {
            var forTask = provider.ForAsync("/tmp/repo");
            await gitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // While rev-parse is held, the sync-context thread must still be able to run work.
            var sideWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sync.Post(_ => sideWork.TrySetResult(), null);
            await sideWork.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(forTask.IsCompleted, Is.False);

            releaseGit.SetResult();
            var gate = await forTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(gate, Is.Not.Null);

            // Cache hit returns the same gate instance.
            var again = await provider.ForAsync("/tmp/repo");
            Assert.That(again, Is.SameAs(gate));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Test]
    public async Task Concurrent_ForAsync_Shares_Single_RevParse()
    {
        var calls = 0;
        var releaseGit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = Substitute.For<IGitProcessRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<GitProcessOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    entered.TrySetResult();
                await releaseGit.Task.ConfigureAwait(false);
                return new GitCommandResult(0, "/tmp/repo/.git\n", "", false, false);
            });

        var provider = new RepositoryGateProvider(runner);
        var first = provider.ForAsync("/tmp/repo");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = provider.ForAsync("/tmp/repo");

        releaseGit.SetResult();
        var gates = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(gates[0], Is.SameAs(gates[1]));
        Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
    }

    [Test]
    public async Task ForAsync_CanceledCaller_DoesNot_Fault_Shared_Resolution()
    {
        var calls = 0;
        var releaseGit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = Substitute.For<IGitProcessRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<GitProcessOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    entered.TrySetResult();
                // Shared resolution must ignore caller cancel — do not observe the token.
                await releaseGit.Task.ConfigureAwait(false);
                return new GitCommandResult(0, "/tmp/repo/.git\n", "", false, false);
            });

        var provider = new RepositoryGateProvider(runner);
        using var firstCts = new CancellationTokenSource();
        var first = provider.ForAsync("/tmp/repo", firstCts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        firstCts.Cancel();
        try
        {
            await first;
            Assert.Fail("Expected canceled ForAsync to throw.");
        }
        catch (OperationCanceledException)
        {
            // TaskCanceledException from WaitAsync is fine — shared work must keep going.
        }

        var second = provider.ForAsync("/tmp/repo", CancellationToken.None);
        releaseGit.SetResult();
        var gate = await second.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(gate, Is.Not.Null);
        Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
    }

    /// <summary>Posts to the thread pool so awaiting ForAsync does not freeze the "UI" thread.</summary>
    private sealed class ThreadPoolSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ => d(state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}

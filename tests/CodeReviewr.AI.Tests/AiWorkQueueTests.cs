using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace CodeReviewr.AI.Tests;

public sealed class AiWorkQueueTests
{
    [Test]
    public async Task Enqueue_HigherPriorityItemsRunBeforeLowerPriorityOnes()
    {
        var queue = new AiWorkQueue(NullLogger<AiWorkQueue>.Instance);
        var order = new List<string>();
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Occupy the single-consumer worker so the next three items queue up together before any
        // of them run, letting priority (not enqueue order) decide execution order.
        queue.Enqueue(new AiWorkItem("repo", AiWorkPriority.BackgroundFile, null, async _ =>
        {
            blockerStarted.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            order.Add("blocker");
        }));

        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        queue.Enqueue(new AiWorkItem("repo", AiWorkPriority.BackgroundFile, null, _ =>
        {
            order.Add("background");
            done.TrySetResult();
            return Task.CompletedTask;
        }));
        queue.Enqueue(new AiWorkItem("repo", AiWorkPriority.Triage, null, _ =>
        {
            order.Add("triage");
            return Task.CompletedTask;
        }));
        queue.Enqueue(new AiWorkItem("repo", AiWorkPriority.ExplicitUser, null, _ =>
        {
            order.Add("explicit");
            return Task.CompletedTask;
        }));

        gate.TrySetResult();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(order, Is.EqualTo(new[] { "blocker", "explicit", "triage", "background" }));
    }

    [Test]
    public async Task Enqueue_SamePriority_RunsInFifoOrder()
    {
        var queue = new AiWorkQueue(NullLogger<AiWorkQueue>.Instance);
        var order = new List<int>();
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Enqueue(new AiWorkItem("repo", AiWorkPriority.OpenFile, null, async _ =>
        {
            blockerStarted.TrySetResult();
            await gate.Task.ConfigureAwait(false);
        }));
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 3; i++)
        {
            var captured = i;
            queue.Enqueue(new AiWorkItem("repo", AiWorkPriority.OpenFile, null, _ =>
            {
                order.Add(captured);
                if (captured == 2)
                    done.TrySetResult();
                return Task.CompletedTask;
            }));
        }

        gate.TrySetResult();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(order, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public async Task CancelRepository_CancelsRunningItem_AndDropsQueuedWork()
    {
        var queue = new AiWorkQueue(NullLogger<AiWorkQueue>.Instance);
        var executed = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Enqueue(new AiWorkItem("repo", AiWorkPriority.Triage, null, async ct =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                observedCancellation.TrySetResult();
                throw;
            }
        }));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        queue.Enqueue(new AiWorkItem("repo", AiWorkPriority.ExplicitUser, null, _ =>
        {
            executed.Add("should-not-run");
            return Task.CompletedTask;
        }));

        queue.CancelRepository("repo");

        await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Give the worker loop a moment to drain (it should find nothing left to run).
        await Task.Delay(50);

        Assert.That(executed, Is.Empty);
    }

    [Test]
    public async Task CancelRepository_UnknownRepository_DoesNotThrow()
    {
        var queue = new AiWorkQueue(NullLogger<AiWorkQueue>.Instance);

        Assert.DoesNotThrow(() => queue.CancelRepository("never-seen-repo"));

        await queue.DisposeAsync();
    }

    [Test]
    public async Task Enqueue_AfterCancel_StartsANewWorkerAndRuns()
    {
        var queue = new AiWorkQueue(NullLogger<AiWorkQueue>.Instance);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.CancelRepository("repo");

        queue.Enqueue(new AiWorkItem("repo", AiWorkPriority.ExplicitUser, null, _ =>
        {
            done.TrySetResult(true);
            return Task.CompletedTask;
        }));

        var ran = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(ran, Is.True);
    }
}

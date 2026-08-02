using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using NUnit.Framework;

namespace CodeReviewr.App.Tests;

public sealed class DiffWarmStoreTests
{
    private static DiffWarmKey Key(string path = "a.txt") =>
        new("fs", path, DiffTarget.IndexToWorktree.AsWorkingCopy(), DiffOptions.Default);

    private static FileDiff SampleDiff(string path = "a.txt") =>
        UntrackedFileDiff.Create(FilePath.From(path), "hello\n");

    [Test]
    public async Task GetOrStart_SingleFlight_Invokes_Factory_Once()
    {
        using var store = new DiffWarmStore(maxConcurrency: 3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<FileDiff> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            return release.Task.ContinueWith(
                _ => SampleDiff(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        var key = Key();
        var t1 = store.GetOrStart(key, Factory);
        var t2 = store.GetOrStart(key, Factory);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(ReferenceEquals(t1, t2), Is.True);

        release.SetResult();
        var d1 = await t1;
        var d2 = await t2;
        Assert.That(d1, Is.SameAs(d2));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task TryGetCompleted_True_After_Success_With_Timestamp()
    {
        using var store = new DiffWarmStore();
        var key = Key();
        var before = DateTimeOffset.UtcNow;
        var diff = await store.GetOrStart(key, _ => Task.FromResult(SampleDiff()));
        Assert.That(store.TryGetCompleted(key, out DiffWarmEntry? entry), Is.True);
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Diff, Is.SameAs(diff));
        Assert.That(entry.IsStale, Is.False);
        Assert.That(entry.CompletedAt, Is.GreaterThanOrEqualTo(before));
    }

    [Test]
    public async Task SoftInvalidate_Keeps_Completed_And_Marks_Stale()
    {
        using var store = new DiffWarmStore();
        var key = Key();
        var first = await store.GetOrStart(key, _ => Task.FromResult(SampleDiff()));
        store.SoftInvalidateAll();

        Assert.That(store.TryGetCompleted(key, out DiffWarmEntry? entry), Is.True);
        Assert.That(entry!.Diff, Is.SameAs(first));
        Assert.That(entry.IsStale, Is.True);

        var calls = 0;
        var second = await store.GetOrStart(key, _ =>
        {
            calls++;
            return Task.FromResult(SampleDiff("a.txt"));
        });
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(store.TryGetCompleted(key, out DiffWarmEntry? refreshed), Is.True);
        Assert.That(refreshed!.IsStale, Is.False);
        Assert.That(refreshed.Diff, Is.SameAs(second));
    }

    [Test]
    public async Task SoftInvalidateScope_Only_Touches_Matching_Scope()
    {
        using var store = new DiffWarmStore();
        var fs = Key("a.txt");
        var hist = new DiffWarmKey("hist:abc", "a.txt", DiffTarget.HeadToWorktree.AsWorkingCopy(), DiffOptions.Default);
        await store.GetOrStart(fs, _ => Task.FromResult(SampleDiff()));
        await store.GetOrStart(hist, _ => Task.FromResult(SampleDiff()));

        store.SoftInvalidateScope("fs");

        Assert.That(store.TryGetCompleted(fs, out DiffWarmEntry? fsEntry) && fsEntry!.IsStale, Is.True);
        Assert.That(store.TryGetCompleted(hist, out DiffWarmEntry? histEntry) && !histEntry!.IsStale, Is.True);
    }

    [Test]
    public async Task Force_Refetch_Replaces_Value_While_Previous_Readable()
    {
        using var store = new DiffWarmStore();
        var key = Key();
        var first = await store.GetOrStart(key, _ => Task.FromResult(SampleDiff("v1")));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = store.GetOrStart(key, _ =>
        {
            started.TrySetResult();
            return release.Task.ContinueWith(
                _ => SampleDiff("v2"),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }, force: true);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(store.TryGetCompleted(key, out DiffWarmEntry? during), Is.True);
        Assert.That(during!.Diff, Is.SameAs(first));

        release.SetResult();
        var second = await refresh;
        Assert.That(store.TryGetCompleted(key, out DiffWarmEntry? after), Is.True);
        Assert.That(after!.Diff, Is.SameAs(second));
        Assert.That(after.IsStale, Is.False);
    }

    [Test]
    public async Task InvalidatePath_Removes_Completed_Entry()
    {
        using var store = new DiffWarmStore();
        var key = Key("a.txt");
        await store.GetOrStart(key, _ => Task.FromResult(SampleDiff("a.txt")));
        store.InvalidatePath("a.txt");
        Assert.That(store.TryGetCompleted(key, out DiffWarmEntry? _), Is.False);

        var calls = 0;
        await store.GetOrStart(key, _ =>
        {
            calls++;
            return Task.FromResult(SampleDiff("a.txt"));
        });
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void SetMaxConcurrency_Clamps_To_1_Through_8()
    {
        using var store = new DiffWarmStore(maxConcurrency: 4);
        store.SetMaxConcurrency(0);
        Assert.That(store.MaxConcurrencyLimit, Is.EqualTo(1));
        store.SetMaxConcurrency(99);
        Assert.That(store.MaxConcurrencyLimit, Is.EqualTo(8));
        store.SetMaxConcurrency(5);
        Assert.That(store.MaxConcurrencyLimit, Is.EqualTo(5));
    }

    [Test]
    public async Task Concurrency_Cap_Limits_Parallel_Factories()
    {
        using var store = new DiffWarmStore(maxConcurrency: 2);
        var entered = 0;
        var maxSeen = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<FileDiff> Factory(string path, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref entered);
            int observed;
            do
            {
                observed = maxSeen;
            } while (now > observed && Interlocked.CompareExchange(ref maxSeen, now, observed) != observed);

            await gate.Task.WaitAsync(ct);
            Interlocked.Decrement(ref entered);
            return SampleDiff(path);
        }

        var tasks = Enumerable.Range(0, 5)
            .Select(i => store.GetOrStart(Key($"f{i}.txt"), ct => Factory($"f{i}.txt", ct)))
            .ToArray();

        await Task.Delay(50);
        Assert.That(Volatile.Read(ref maxSeen), Is.LessThanOrEqualTo(2));
        gate.SetResult();
        await Task.WhenAll(tasks);
    }
}

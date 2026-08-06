using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GitDelta.AI;

/// <summary>Relative priority of queued AI work. Lower values run first.</summary>
public enum AiWorkPriority
{
    ExplicitUser = 0,
    OpenFile = 1,
    ChangeBriefing = 2,
    BackgroundFile = 3,
}

/// <summary>
/// A unit of AI work targeting a single repository. <paramref name="Path"/> is set for file-scoped
/// work (used by <see cref="AiWorkQueue.DemoteFileWork"/>); it is null for PR-scoped work such as triage.
/// </summary>
public sealed record AiWorkItem(
    string RepositoryKey,
    AiWorkPriority Priority,
    string? Path,
    Func<CancellationToken, Task> Execute);

/// <summary>
/// Single-consumer priority queue per repository: at most one AI turn runs at a time for a given
/// repository, in priority order, so an explicit user request always jumps ahead of background
/// file-depth work without the coordinator needing its own locking.
/// </summary>
public sealed class AiWorkQueue(ILogger<AiWorkQueue> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RepoQueue> _repos = new();

    public void Enqueue(AiWorkItem item)
    {
        var repo = _repos.GetOrAdd(item.RepositoryKey, static _ => new RepoQueue());
        lock (repo.Lock)
        {
            repo.Queue.Enqueue(item, (item.Priority, repo.NextSequence++));
            EnsureWorkerRunning(item.RepositoryKey, repo);
        }
    }

    /// <summary>Cancels the currently running item (if any) and drops everything queued for this repository.</summary>
    public void CancelRepository(string repositoryKey)
    {
        if (!_repos.TryGetValue(repositoryKey, out var repo))
            return;

        lock (repo.Lock)
        {
            repo.Cts.Cancel();
            repo.Cts.Dispose();
            repo.Cts = new CancellationTokenSource();
            repo.Queue.Clear();
        }
    }

    /// <summary>Re-priorities all queued (not yet started) items for <paramref name="path"/> in this repository.</summary>
    public void DemoteFileWork(string repositoryKey, string path, AiWorkPriority newPriority)
    {
        if (!_repos.TryGetValue(repositoryKey, out var repo))
            return;

        lock (repo.Lock)
        {
            var pending = new List<AiWorkItem>();
            while (repo.Queue.TryDequeue(out var item, out _))
                pending.Add(item);

            foreach (var item in pending)
            {
                var effective = string.Equals(item.Path, path, StringComparison.Ordinal)
                    ? item with { Priority = newPriority }
                    : item;
                repo.Queue.Enqueue(effective, (effective.Priority, repo.NextSequence++));
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var repo in _repos.Values)
            repo.Cts.Cancel();

        return ValueTask.CompletedTask;
    }

    private void EnsureWorkerRunning(string repositoryKey, RepoQueue repo)
    {
        if (repo.Worker is { IsCompleted: false })
            return;

        repo.Worker = Task.Run(() => ProcessAsync(repositoryKey, repo));
    }

    private async Task ProcessAsync(string repositoryKey, RepoQueue repo)
    {
        while (true)
        {
            AiWorkItem item;
            CancellationToken ct;
            lock (repo.Lock)
            {
                if (!repo.Queue.TryDequeue(out item!, out _))
                    return;

                ct = repo.Cts.Token;
            }

            try
            {
                await item.Execute(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when CancelRepository() is called mid-item.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI work item failed for repository {RepositoryKey}.", repositoryKey);
            }
        }
    }

    private sealed class RepoQueue
    {
        public readonly Lock Lock = new();
        public readonly PriorityQueue<AiWorkItem, (AiWorkPriority Priority, long Sequence)> Queue = new(
            Comparer<(AiWorkPriority Priority, long Sequence)>.Create((a, b) =>
            {
                var byPriority = a.Priority.CompareTo(b.Priority);
                return byPriority != 0 ? byPriority : a.Sequence.CompareTo(b.Sequence);
            }));

        public CancellationTokenSource Cts = new();
        public Task? Worker;
        public long NextSequence;
    }
}

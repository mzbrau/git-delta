namespace CodeReviewr.Core.Abstractions;

/// <summary>
/// Watches a repository for changes that should trigger a status refresh.
/// Implementations live in CodeReviewr.Git; App consumes only this contract.
/// </summary>
public interface IRepositoryWatcher : IDisposable
{
    event Action? RefreshRequested;
    event Action? OfferFsmonitor;

    TimeSpan Debounce { get; set; }
    TimeSpan StatusSlowThreshold { get; set; }

    void WatchRepository(string repositoryPath);
    void WatchDisplayedFile(string? fullPath);
    void NotifyOwnWriteCompleted();
    void NotifyWindowActivated();
    void RecordStatusDuration(TimeSpan duration);
}

/// <summary>Opt-in helper to enable <c>core.fsmonitor</c> in a repository's local config.</summary>
public interface IFsmonitorService
{
    Task EnableAsync(string repositoryPath, CancellationToken ct = default);
}

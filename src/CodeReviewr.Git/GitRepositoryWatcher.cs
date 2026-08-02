using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.Git;

/// <summary>
/// Watches .git only (plus optional bounded paths for the open file).
/// Debounces bursts and raises RefreshRequested.
/// </summary>
public sealed class GitRepositoryWatcher : IRepositoryWatcher
{
    private FileSystemWatcher? _gitWatcher;
    private FileSystemWatcher? _fileWatcher;
    private CancellationTokenSource? _debounceCts;
    private readonly object _lock = new();
    private int _burstCount;
    private const int MaxBurstBeforeFull = 50;

    public event Action? RefreshRequested;
    public event Action? OfferFsmonitor;

    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan StatusSlowThreshold { get; set; } = TimeSpan.FromSeconds(2);

    public void WatchRepository(string repositoryPath)
    {
        DisposeWatchers();
        var gitDir = Path.Combine(repositoryPath, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir)) return;

        var watchPath = Directory.Exists(gitDir) ? gitDir : repositoryPath;
        _gitWatcher = new FileSystemWatcher(watchPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        _gitWatcher.Changed += OnChanged;
        _gitWatcher.Created += OnChanged;
        _gitWatcher.Deleted += OnChanged;
        _gitWatcher.Renamed += OnChanged;
        _gitWatcher.EnableRaisingEvents = true;
    }

    public void WatchDisplayedFile(string? fullPath)
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        if (fullPath is null || !File.Exists(fullPath)) return;
        var dir = Path.GetDirectoryName(fullPath)!;
        var name = Path.GetFileName(fullPath);
        _fileWatcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _fileWatcher.Changed += OnChanged;
        _fileWatcher.EnableRaisingEvents = true;
    }

    public void NotifyOwnWriteCompleted() => ScheduleRefresh();

    public void NotifyWindowActivated() => ScheduleRefresh();

    public void RecordStatusDuration(TimeSpan duration)
    {
        if (duration >= StatusSlowThreshold)
            OfferFsmonitor?.Invoke();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        Interlocked.Increment(ref _burstCount);
        if (_burstCount >= MaxBurstBeforeFull)
        {
            Interlocked.Exchange(ref _burstCount, 0);
            RefreshRequested?.Invoke();
            return;
        }
        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        lock (_lock)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Debounce, token);
                    Interlocked.Exchange(ref _burstCount, 0);
                    RefreshRequested?.Invoke();
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

    private void DisposeWatchers()
    {
        _gitWatcher?.Dispose();
        _gitWatcher = null;
        _fileWatcher?.Dispose();
        _fileWatcher = null;
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        DisposeWatchers();
    }
}

/// <summary>Opt-in helper to enable core.fsmonitor in the user's repository config.</summary>
public sealed class FsmonitorService(IGitProcessRunner runner) : IFsmonitorService
{
    public Task EnableAsync(string repositoryPath, CancellationToken ct = default) =>
        runner.RunAsync(repositoryPath, ["config", "core.fsmonitor", "true"], ct: ct);
}

/// <summary>Opt-in helper to enable core.fsmonitor in the user's repository config.</summary>
[Obsolete("Use FsmonitorService via IFsmonitorService.")]
public sealed class FsmonitorPrompt(IGitProcessRunner runner)
{
    public Task EnableAsync(string repositoryPath, CancellationToken ct = default) =>
        runner.RunAsync(repositoryPath, ["config", "core.fsmonitor", "true"], ct: ct);
}

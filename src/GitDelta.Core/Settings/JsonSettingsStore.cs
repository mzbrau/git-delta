using System.Text.Json;
using GitDelta.Core.Abstractions;

namespace GitDelta.Core.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _current = new();

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitDelta",
            "settings.json");
    }

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>
    /// Synchronous load for startup. Prefer this over <see cref="LoadAsync"/> when called
    /// via GetResult on the UI thread — async load can deadlock on the Avalonia sync context.
    /// </summary>
    public void Load()
    {
        if (!File.Exists(_path))
            return;

        var json = File.ReadAllText(_path);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);
        if (loaded is not null)
        {
            loaded.Shortcuts ??= KeyboardShortcutBindings.CreateDefaults();
            lock (_lock)
                _current = loaded;
        }
    }

    public Task LoadAsync(CancellationToken ct = default)
    {
        Load();
        return Task.CompletedTask;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-clone under the store lock so waiters always persist the latest in-memory state.
            AppSettings snapshot;
            lock (_lock)
                snapshot = Clone(_current);

            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    new JsonSerializerOptions { WriteIndented = true },
                    ct).ConfigureAwait(false);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_lock)
            mutate(_current);
    }

    public void AddRecentRepository(string path)
    {
        Update(s =>
        {
            s.RecentRepositories.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            s.RecentRepositories.Insert(0, path);
            if (s.RecentRepositories.Count > 20)
                s.RecentRepositories.RemoveRange(20, s.RecentRepositories.Count - 20);
        });
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        GitExecutablePath = s.GitExecutablePath,
        Theme = s.Theme,
        FontSize = s.FontSize,
        DefaultDiffMode = s.DefaultDiffMode,
        IgnoreWhitespace = s.IgnoreWhitespace,
        ShowWhitespace = s.ShowWhitespace,
        DiffAlgorithm = s.DiffAlgorithm,
        ContextLines = s.ContextLines,
        SyntaxHighlightingSizeCapBytes = s.SyntaxHighlightingSizeCapBytes,
        SyntaxHighlightingLineLengthCap = s.SyntaxHighlightingLineLengthCap,
        RecentRepositories = [.. s.RecentRepositories],
        WindowWidth = s.WindowWidth,
        WindowHeight = s.WindowHeight,
        NavigatorWidth = s.NavigatorWidth,
        FileListWidth = s.FileListWidth,
        NavigatorCollapsed = s.NavigatorCollapsed,
        FileStatusListLayout = s.FileStatusListLayout,
        HistoryFileListLayout = s.HistoryFileListLayout,
        PullRequestFileListLayout = s.PullRequestFileListLayout,
        SimulateSlowGit = s.SimulateSlowGit,
        DiffPrefetchConcurrency = s.DiffPrefetchConcurrency,
        DiffPrefetchDripDelayMs = s.DiffPrefetchDripDelayMs,
        DiffPrefetchIndicatorThrottleMs = s.DiffPrefetchIndicatorThrottleMs,
        DiffPrefetchPriorityPaths = s.DiffPrefetchPriorityPaths,
        DiffPrefetchNeighborRadius = s.DiffPrefetchNeighborRadius,
        MaxDiffPatchBytes = s.MaxDiffPatchBytes,
        DiffCacheCapacity = s.DiffCacheCapacity,
        AiAssistanceEnabled = s.AiAssistanceEnabled,
        AiUseDedicatedCopilotToken = s.AiUseDedicatedCopilotToken,
        AiModelOverride = s.AiModelOverride,
        AiReasoningEffort = s.AiReasoningEffort,
        AiReviewRules = s.AiReviewRules,
        AiTurnBudget = s.AiTurnBudget,
        AiFileBriefingMinChangePercent = s.AiFileBriefingMinChangePercent,
        AiFileBriefingMinLinesChanged = s.AiFileBriefingMinLinesChanged,
        AiTurnTimeoutSeconds = s.AiTurnTimeoutSeconds,
        AiRunTimeoutSeconds = s.AiRunTimeoutSeconds,
        AiPathDenylist = [.. s.AiPathDenylist],
        AiExcludedRepositories = [.. s.AiExcludedRepositories],
        AiDisclosureAcknowledged = s.AiDisclosureAcknowledged,
        AiExportRetentionDays = s.AiExportRetentionDays,
        AiLargePrFileThreshold = s.AiLargePrFileThreshold,
        TicketFromBranchRegex = s.TicketFromBranchRegex,
        DevelopmentFolder = s.DevelopmentFolder,
        RepositoryScanDepth = s.RepositoryScanDepth,
        RepositoryScanIgnore = [.. s.RepositoryScanIgnore],
        EnterpriseHostUrls = [.. s.EnterpriseHostUrls],
        Accounts = [.. s.Accounts],
        RepositoryBindings = [.. s.RepositoryBindings],
        Shortcuts = (s.Shortcuts ?? KeyboardShortcutBindings.CreateDefaults()).Clone(),
    };
}

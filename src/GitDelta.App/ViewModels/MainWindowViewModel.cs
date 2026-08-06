using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDelta.App.Services;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.GitHub;
using GitDelta.Review;

namespace GitDelta.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const double CollapsedNavigatorWidth = 36;
    private const double MinNavigatorWidth = 160;
    private const double MinFileListWidth = 200;

    private readonly ISettingsStore _settings;
    private readonly NotificationService _notifications;
    private readonly IAccountService _accounts;
    private readonly IRepositoryLocator _repositoryLocator;
    private readonly IAIReviewService _ai;
    private readonly IConfirmDialog _confirm;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private CancellationTokenSource? _catalogCts;
    private string? _requestedOpenPath;
    private string? _catalogRoot;
    private bool _catalogLoaded;
    private double _expandedNavigatorWidth;

    public MainWindowViewModel(
        WorkingCopyViewModel workingCopy,
        ReviewViewModel review,
        DiagnosticsOverlayViewModel diagnostics,
        GitConsoleViewModel gitConsole,
        ISettingsStore settings,
        NotificationService notifications,
        IAccountService accounts,
        IRepositoryLocator repositoryLocator,
        IConfirmDialog confirm,
        IAIReviewService? ai = null)
    {
        WorkingCopy = workingCopy;
        Review = review;
        Diagnostics = diagnostics;
        GitConsole = gitConsole;
        _settings = settings;
        _notifications = notifications;
        _accounts = accounts;
        _repositoryLocator = repositoryLocator;
        _confirm = confirm;
        _ai = ai ?? NullAIReviewService.Instance;
        RecentRepositories = new(_settings.Current.RecentRepositories);
        DevelopmentFolder = _settings.Current.DevelopmentFolder ?? "";
        GitHubAccounts = new(_settings.Current.Accounts);
        EnterpriseHostUrls = new(_settings.Current.EnterpriseHostUrls);
        NewGitHubHost = "github.com";
        NewEnterpriseHost = "";
        DefaultDiffMode = _settings.Current.DefaultDiffMode;
        _theme = string.IsNullOrWhiteSpace(_settings.Current.Theme) ? "System" : _settings.Current.Theme;
        _simulateSlowGit = _settings.Current.SimulateSlowGit;
        _diffPrefetchConcurrency = DiffWarmStore.ClampConcurrency(
            _settings.Current.DiffPrefetchConcurrency <= 0
                ? DiffWarmStore.DefaultConcurrency
                : _settings.Current.DiffPrefetchConcurrency);
        WorkingCopy.SetDiffPrefetchConcurrency(_diffPrefetchConcurrency);

        WorkingCopy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WorkingCopyViewModel.IsHistoryMode))
            {
                OnPropertyChanged(nameof(ShowFileStatusPane));
                OnPropertyChanged(nameof(ShowPullRequestPane));
                OnPropertyChanged(nameof(ShowHistoryPane));
                OnPropertyChanged(nameof(ShowMainFileDiffSplitter));
            }

            if (e.PropertyName is nameof(WorkingCopyViewModel.RepositoryPath)
                or nameof(WorkingCopyViewModel.CurrentBranch)
                or nameof(WorkingCopyViewModel.HasRepository))
            {
                NotifyRepositorySwitcherDisplayChanged();
                UpdateScannedCurrentFlags();
            }
        };
        Review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReviewViewModel.IsPullRequestMode)
                or nameof(ReviewViewModel.WorkspaceMode))
            {
                OnPropertyChanged(nameof(ShowFileStatusPane));
                OnPropertyChanged(nameof(ShowPullRequestPane));
                OnPropertyChanged(nameof(ShowHistoryPane));
                OnPropertyChanged(nameof(ShowMainFileDiffSplitter));

                // Opening a PR must leave History so the History grid cannot cover PR panes
                // after the loading overlay clears.
                if (Review.IsPullRequestMode && WorkingCopy.IsHistoryMode)
                    WorkingCopy.SelectFileStatusCommand.Execute(null);
            }
        };

        _expandedNavigatorWidth = Math.Max(MinNavigatorWidth, _settings.Current.NavigatorWidth);
        _navigatorWidth = _settings.Current.NavigatorCollapsed
            ? CollapsedNavigatorWidth
            : _expandedNavigatorWidth;
        _fileListWidth = Math.Max(MinFileListWidth, _settings.Current.FileListWidth);
        _isNavigatorCollapsed = _settings.Current.NavigatorCollapsed;
        _windowWidth = _settings.Current.WindowWidth;
        _windowHeight = _settings.Current.WindowHeight;

        _aiAssistanceEnabled = _settings.Current.AiAssistanceEnabled;
        _aiUseDedicatedCopilotToken = _settings.Current.AiUseDedicatedCopilotToken;
        _aiModelOverride = _settings.Current.AiModelOverride ?? "";
        _aiReasoningEffort = _settings.Current.AiReasoningEffort ?? "";
        _aiReviewRules = _settings.Current.AiReviewRules;
        _aiTurnBudget = _settings.Current.AiTurnBudget;
        _aiFileBriefingMinChangePercent = _settings.Current.AiFileBriefingMinChangePercent;
        _aiFileBriefingMinLinesChanged = _settings.Current.AiFileBriefingMinLinesChanged;
        _aiTurnTimeoutSeconds = _settings.Current.AiTurnTimeoutSeconds;
        _aiRunTimeoutSeconds = _settings.Current.AiRunTimeoutSeconds;
        _aiPathDenylistText = string.Join('\n', _settings.Current.AiPathDenylist);
        _aiExcludedRepositoriesText = string.Join('\n', _settings.Current.AiExcludedRepositories);
        _aiDisclosureAcknowledged = _settings.Current.AiDisclosureAcknowledged;
        _aiExportRetentionDays = _settings.Current.AiExportRetentionDays;
        _aiLargePrFileThreshold = _settings.Current.AiLargePrFileThreshold;
        _ticketFromBranchRegex = string.IsNullOrWhiteSpace(_settings.Current.TicketFromBranchRegex)
            ? TicketFromBranch.DefaultRegex
            : _settings.Current.TicketFromBranchRegex;
    }

    public WorkingCopyViewModel WorkingCopy { get; }
    public ReviewViewModel Review { get; }
    public DiagnosticsOverlayViewModel Diagnostics { get; }
    public GitConsoleViewModel GitConsole { get; }
    public NotificationService Notifications => _notifications;

    public System.Collections.ObjectModel.ObservableCollection<GitHubAccountSettings> GitHubAccounts { get; }
    public System.Collections.ObjectModel.ObservableCollection<string> EnterpriseHostUrls { get; }

    public string[] SettingsCategories { get; } = ["General", "Accounts", "Diff", "Git", "AI", "Diagnostics"];

    [ObservableProperty] private string _selectedSettingsCategory = "General";
    [ObservableProperty] private GitHubAccountSettings? _selectedGitHubAccount;
    [ObservableProperty] private bool _isAddingGitHubAccount;

    [ObservableProperty] private string _developmentFolder = "";
    [ObservableProperty] private string _newGitHubHost = "github.com";
    [ObservableProperty] private string _newGitHubToken = "";
    [ObservableProperty] private string _newEnterpriseHost = "";
    [ObservableProperty] private string _reauthToken = "";
    [ObservableProperty] private GitHubAccountSettings? _reauthAccount;

    [ObservableProperty] private GitExecutableInfo? _gitInfo;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private DiffViewMode _defaultDiffMode;
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private bool _simulateSlowGit;
    [ObservableProperty] private int _diffPrefetchConcurrency = DiffWarmStore.DefaultConcurrency;
    [ObservableProperty] private double _navigatorWidth;
    [ObservableProperty] private double _fileListWidth;
    [ObservableProperty] private bool _isNavigatorCollapsed;
    [ObservableProperty] private double _windowWidth;
    [ObservableProperty] private double _windowHeight;
    [ObservableProperty] private string _repositoryFilter = "";
    [ObservableProperty] private bool _isScanningRepositories;
    [ObservableProperty] private bool _isOpeningRepository;

    // --- AI settings (Phase 3) ---
    [ObservableProperty] private bool _aiAssistanceEnabled;
    [ObservableProperty] private bool _aiUseDedicatedCopilotToken;
    [ObservableProperty] private string _aiModelOverride = "";
    [ObservableProperty] private string _aiReasoningEffort = "";
    [ObservableProperty] private string _aiReviewRules = "";
    [ObservableProperty] private int _aiTurnBudget = 100;
    [ObservableProperty] private int _aiFileBriefingMinChangePercent = 25;
    [ObservableProperty] private int _aiFileBriefingMinLinesChanged = 10;
    [ObservableProperty] private int _aiTurnTimeoutSeconds = 180;
    [ObservableProperty] private int _aiRunTimeoutSeconds = 1800;
    [ObservableProperty] private string _aiPathDenylistText = "";
    [ObservableProperty] private string _aiExcludedRepositoriesText = "";
    [ObservableProperty] private bool _aiDisclosureAcknowledged;
    [ObservableProperty] private int _aiExportRetentionDays = 14;
    [ObservableProperty] private int _aiLargePrFileThreshold = 30;
    [ObservableProperty] private string _ticketFromBranchRegex = TicketFromBranch.DefaultRegex;
    [ObservableProperty] private bool _isTestingAiConnection;
    [ObservableProperty] private string? _aiConnectionTestResult;
    [ObservableProperty] private bool _isRefreshingAiModels;
    [ObservableProperty] private bool _isClearingAiData;

    public ObservableCollection<string> AiAvailableModels { get; } = [];
    public string[] AiReasoningEffortOptions { get; } = ["", "low", "medium", "high", "xhigh"];

    public ObservableCollection<string> RecentRepositories { get; }
    public ObservableCollection<RepositoryEntryViewModel> ScannedRepositories { get; } = [];
    public ObservableCollection<RepositoryEntryViewModel> FilteredRepositories { get; } = [];

    public bool HasRecentRepositories => RecentRepositories.Count > 0;
    public bool HasFilteredRepositories => FilteredRepositories.Count > 0;
    public bool ShowRepositoryCatalogEmpty =>
        !IsScanningRepositories && FilteredRepositories.Count == 0;

    public bool ShowRepositoryCatalogScanning =>
        IsScanningRepositories && FilteredRepositories.Count == 0;

    public string RepositoryCatalogEmptyText =>
        string.IsNullOrWhiteSpace(RepositoryFilter)
            ? "No repositories found under Development Folder"
            : "No matching repositories";

    public string CurrentRepositoryName =>
        WorkingCopy.HasRepository && !string.IsNullOrWhiteSpace(WorkingCopy.RepositoryPath)
            ? Path.GetFileName(WorkingCopy.RepositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : "Select repository";

    public string CurrentRepositorySubtitle
    {
        get
        {
            if (!WorkingCopy.HasRepository || string.IsNullOrWhiteSpace(WorkingCopy.RepositoryPath))
                return "No repository open";

            var pathLabel = FormatRepositoryPathLabel(WorkingCopy.RepositoryPath);
            var branch = WorkingCopy.CurrentBranch;
            return string.IsNullOrWhiteSpace(branch) ? pathLabel : $"{pathLabel}  ·  {branch}";
        }
    }

    public string CurrentRepositoryTooltip =>
        WorkingCopy.HasRepository && !string.IsNullOrWhiteSpace(WorkingCopy.RepositoryPath)
            ? WorkingCopy.RepositoryPath
            : "Select a repository";
    public bool HasGitHubAccounts => GitHubAccounts.Count > 0;
    public bool IsSettingsGeneral => SelectedSettingsCategory == "General";
    public bool IsSettingsAccounts => SelectedSettingsCategory == "Accounts";
    public bool IsSettingsDiff => SelectedSettingsCategory == "Diff";
    public bool IsSettingsGit => SelectedSettingsCategory == "Git";
    public bool IsSettingsAi => SelectedSettingsCategory == "AI";
    public bool IsSettingsDiagnostics => SelectedSettingsCategory == "Diagnostics";
    public bool ShowAddAccountForm => IsAddingGitHubAccount || SelectedGitHubAccount is null;
    public bool ShowAccountDetail => !IsAddingGitHubAccount && SelectedGitHubAccount is not null;

    public GridLength NavigatorColumnWidth => new(NavigatorWidth);
    public GridLength FileListColumnWidth => new(FileListWidth);
    public bool ShowFileStatusPane => !WorkingCopy.IsHistoryMode && !Review.IsPullRequestMode;
    public bool ShowPullRequestPane => Review.IsPullRequestMode;
    public bool ShowHistoryPane => WorkingCopy.IsHistoryMode && !Review.IsPullRequestMode;
    public bool ShowMainFileDiffSplitter => ShowFileStatusPane || ShowPullRequestPane;

    partial void OnNavigatorWidthChanged(double value) => OnPropertyChanged(nameof(NavigatorColumnWidth));
    partial void OnFileListWidthChanged(double value) => OnPropertyChanged(nameof(FileListColumnWidth));
    partial void OnSelectedSettingsCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsSettingsGeneral));
        OnPropertyChanged(nameof(IsSettingsAccounts));
        OnPropertyChanged(nameof(IsSettingsDiff));
        OnPropertyChanged(nameof(IsSettingsGit));
        OnPropertyChanged(nameof(IsSettingsAi));
        OnPropertyChanged(nameof(IsSettingsDiagnostics));

        if (value == "Accounts" && !IsAddingGitHubAccount && SelectedGitHubAccount is null && GitHubAccounts.Count > 0)
            SelectedGitHubAccount = GitHubAccounts[0];

        if (value == "AI" && AiAvailableModels.Count == 0)
            _ = RefreshAiModelsAsync();
    }

    partial void OnSelectedGitHubAccountChanged(GitHubAccountSettings? value)
    {
        if (value is not null)
            IsAddingGitHubAccount = false;
        OnPropertyChanged(nameof(ShowAddAccountForm));
        OnPropertyChanged(nameof(ShowAccountDetail));
    }

    partial void OnIsAddingGitHubAccountChanged(bool value)
    {
        if (value)
            SelectedGitHubAccount = null;
        OnPropertyChanged(nameof(ShowAddAccountForm));
        OnPropertyChanged(nameof(ShowAccountDetail));
    }
    partial void OnIsNavigatorCollapsedChanged(bool value)
    {
        if (value)
        {
            if (NavigatorWidth > CollapsedNavigatorWidth + 1)
                _expandedNavigatorWidth = NavigatorWidth;
            NavigatorWidth = CollapsedNavigatorWidth;
        }
        else
        {
            NavigatorWidth = Math.Max(MinNavigatorWidth, _expandedNavigatorWidth);
        }

        _settings.Update(s =>
        {
            s.NavigatorCollapsed = value;
            if (!value)
                s.NavigatorWidth = NavigatorWidth;
        });
        _ = _settings.SaveAsync();
    }

    public void CaptureColumnWidthsFromGrid(Grid grid)
    {
        if (grid.ColumnDefinitions.Count < 5) return;

        var nav = grid.ColumnDefinitions[0].ActualWidth;
        var files = grid.ColumnDefinitions[2].ActualWidth;

        if (!IsNavigatorCollapsed && nav >= MinNavigatorWidth)
        {
            NavigatorWidth = nav;
            _expandedNavigatorWidth = nav;
        }

        if (files >= MinFileListWidth)
            FileListWidth = files;

        PersistLayout();
    }

    public void PersistLayout()
    {
        _settings.Update(s =>
        {
            s.WindowWidth = WindowWidth;
            s.WindowHeight = WindowHeight;
            s.NavigatorWidth = IsNavigatorCollapsed ? _expandedNavigatorWidth : NavigatorWidth;
            s.FileListWidth = FileListWidth;
            s.NavigatorCollapsed = IsNavigatorCollapsed;
        });
        _ = _settings.SaveAsync();
    }

    public async Task OpenRepositoryPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _requestedOpenPath = path;
        if (!await _openGate.WaitAsync(0).ConfigureAwait(true))
            return;

        try
        {
            while (_requestedOpenPath is { } target)
            {
                _requestedOpenPath = null;
                IsOpeningRepository = true;
                try
                {
                    StatusText = $"Opening {target}…";
                    await WorkingCopy.OpenAsync(target).ConfigureAwait(true);
                    if (_requestedOpenPath is not null)
                        continue;

                    AddRecent(target);
                    StatusText = WorkingCopy.CurrentBranch is null
                        ? target
                        : $"{WorkingCopy.CurrentBranch} — {target}";
                    NotifyRepositorySwitcherDisplayChanged();
                    UpdateScannedCurrentFlags();
                }
                catch (Exception ex)
                {
                    if (_requestedOpenPath is not null)
                        continue;

                    _notifications.Error($"Failed to open repository: {ex.Message}",
                        () => _ = OpenRepositoryPathAsync(target), ex);
                    StatusText = "Failed to open repository";
                }
            }
        }
        finally
        {
            IsOpeningRepository = false;
            _openGate.Release();
            if (_requestedOpenPath is { } pending)
                _ = OpenRepositoryPathAsync(pending);
        }
    }

    [RelayCommand]
    private async Task SelectRepositoryAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (string.Equals(path, WorkingCopy.RepositoryPath, StringComparison.OrdinalIgnoreCase)
            && !IsOpeningRepository)
            return;

        await OpenRepositoryPathAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RefreshRepositoryCatalogAsync() => RefreshRepositoryCatalogCoreAsync(force: true);

    public Task EnsureRepositoryCatalogAsync() => RefreshRepositoryCatalogCoreAsync(force: false);

    private async Task RefreshRepositoryCatalogCoreAsync(bool force)
    {
        var root = _settings.Current.DevelopmentFolder?.Trim();
        if (!force
            && _catalogLoaded
            && string.Equals(_catalogRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!force && IsScanningRepositories
            && string.Equals(_catalogRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _catalogCts?.Cancel();
        _catalogCts?.Dispose();
        var cts = new CancellationTokenSource();
        _catalogCts = cts;
        var ct = cts.Token;

        _catalogLoaded = false;
        _catalogRoot = root;

        await InvokeOnUiAsync(() =>
        {
            IsScanningRepositories = true;
            ScannedRepositories.Clear();
            RebuildFilteredRepositories();
        }).ConfigureAwait(false);

        try
        {
            var batch = new List<RepositoryEntryViewModel>();
            await foreach (var located in _repositoryLocator.ScanLocalAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                batch.Add(CreateRepositoryEntry(located.LocalPath, located.CurrentBranch));

                if (batch.Count < 8)
                    continue;

                var toAdd = batch;
                batch = [];
                await InvokeOnUiAsync(() => AppendScannedRepositories(toAdd)).ConfigureAwait(false);
            }

            if (batch.Count > 0)
                await InvokeOnUiAsync(() => AppendScannedRepositories(batch)).ConfigureAwait(false);

            if (_catalogCts == cts)
                _catalogLoaded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            await InvokeOnUiAsync(() =>
                _notifications.Error($"Failed to scan repositories: {ex.Message}",
                    () => _ = RefreshRepositoryCatalogAsync(), ex)).ConfigureAwait(false);
        }
        finally
        {
            if (_catalogCts == cts)
            {
                await InvokeOnUiAsync(() =>
                {
                    IsScanningRepositories = false;
                    OnPropertyChanged(nameof(ShowRepositoryCatalogEmpty));
                }).ConfigureAwait(false);
            }
        }
    }

    private void AppendScannedRepositories(List<RepositoryEntryViewModel> entries)
    {
        var current = WorkingCopy.RepositoryPath;
        foreach (var entry in entries)
        {
            entry.IsCurrent = current is not null
                              && string.Equals(entry.Path, current, StringComparison.OrdinalIgnoreCase);
            ScannedRepositories.Add(entry);
        }

        RebuildFilteredRepositories();
    }

    private RepositoryEntryViewModel CreateRepositoryEntry(string path, string? branch)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name))
            name = trimmed;

        return new RepositoryEntryViewModel(path, name, FormatRepositoryPathLabel(path), branch);
    }

    private string FormatRepositoryPathLabel(string path)
    {
        var root = _settings.Current.DevelopmentFolder?.Trim();
        if (!string.IsNullOrWhiteSpace(root))
        {
            try
            {
                var relative = Path.GetRelativePath(root, path);
                if (!relative.StartsWith("..", StringComparison.Ordinal)
                    && !Path.IsPathRooted(relative))
                {
                    return relative.Replace(Path.DirectorySeparatorChar, '/');
                }
            }
            catch
            {
                // Fall through to absolute path.
            }
        }

        return path;
    }

    private void RebuildFilteredRepositories()
    {
        FilteredRepositories.Clear();
        foreach (var entry in ScannedRepositories
                     .Where(e => e.MatchesFilter(RepositoryFilter))
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            FilteredRepositories.Add(entry);

        OnPropertyChanged(nameof(HasFilteredRepositories));
        OnPropertyChanged(nameof(ShowRepositoryCatalogEmpty));
        OnPropertyChanged(nameof(ShowRepositoryCatalogScanning));
    }

    private void UpdateScannedCurrentFlags()
    {
        var current = WorkingCopy.RepositoryPath;
        foreach (var entry in ScannedRepositories)
        {
            entry.IsCurrent = current is not null
                              && string.Equals(entry.Path, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void NotifyRepositorySwitcherDisplayChanged()
    {
        OnPropertyChanged(nameof(CurrentRepositoryName));
        OnPropertyChanged(nameof(CurrentRepositorySubtitle));
        OnPropertyChanged(nameof(CurrentRepositoryTooltip));
    }

    partial void OnRepositoryFilterChanged(string value)
    {
        RebuildFilteredRepositories();
        OnPropertyChanged(nameof(RepositoryCatalogEmptyText));
    }

    partial void OnIsScanningRepositoriesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRepositoryCatalogEmpty));
        OnPropertyChanged(nameof(ShowRepositoryCatalogScanning));
    }

    private static async Task InvokeOnUiAsync(Action action)
    {
        try
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            if (Avalonia.Application.Current is null)
            {
                action();
                return;
            }

            await dispatcher.InvokeAsync(action);
        }
        catch (InvalidOperationException)
        {
            action();
        }
    }

    public async Task TryOpenLastRepositoryAsync()
    {
        if (RecentRepositories.Count == 0) return;
        var path = RecentRepositories[0];
        if (!Directory.Exists(path))
        {
            RecentRepositories.Remove(path);
            _settings.Update(s =>
                s.RecentRepositories.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
            _ = _settings.SaveAsync();
            OnPropertyChanged(nameof(HasRecentRepositories));
            return;
        }

        await OpenRepositoryPathAsync(path);
    }

    private void AddRecent(string path)
    {
        _settings.Update(s =>
        {
            s.RecentRepositories.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            s.RecentRepositories.Insert(0, path);
            if (s.RecentRepositories.Count > 20)
                s.RecentRepositories.RemoveRange(20, s.RecentRepositories.Count - 20);
        });
        _ = _settings.SaveAsync();
        RecentRepositories.Remove(path);
        RecentRepositories.Insert(0, path);
        OnPropertyChanged(nameof(HasRecentRepositories));
    }

    [RelayCommand]
    private async Task RefreshAsync() => await WorkingCopy.RefreshAsync();

    [RelayCommand]
    private void OpenSettings()
    {
        ShowSettings = true;
        if (SelectedSettingsCategory == "Accounts" && !IsAddingGitHubAccount && SelectedGitHubAccount is null && GitHubAccounts.Count > 0)
            SelectedGitHubAccount = GitHubAccounts[0];
    }

    [RelayCommand]
    private void CloseSettings() => ShowSettings = false;

    [RelayCommand]
    private void BeginAddGitHubAccount()
    {
        IsAddingGitHubAccount = true;
        NewGitHubToken = "";
        if (string.IsNullOrWhiteSpace(NewGitHubHost))
            NewGitHubHost = "github.com";
    }

    [RelayCommand]
    private void SelectNav(string nav)
    {
        // Phase 1 only supports Working Copy; other nav sections are visual stubs.
    }

    partial void OnDefaultDiffModeChanged(DiffViewMode value)
    {
        _settings.Update(s => s.DefaultDiffMode = value);
        _ = _settings.SaveAsync();
        WorkingCopy.ViewMode = value;
    }

    partial void OnThemeChanged(string value)
    {
        _settings.Update(s => s.Theme = value);
        _ = _settings.SaveAsync();
        global::GitDelta.App.App.ApplyTheme(value);
    }

    public int[] DiffPrefetchConcurrencyOptions { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    public int DiffPrefetchConcurrencyIndex
    {
        get
        {
            var idx = Array.IndexOf(DiffPrefetchConcurrencyOptions, DiffPrefetchConcurrency);
            return idx >= 0 ? idx : 3;
        }
        set
        {
            if (value < 0 || value >= DiffPrefetchConcurrencyOptions.Length) return;
            DiffPrefetchConcurrency = DiffPrefetchConcurrencyOptions[value];
        }
    }

    partial void OnSimulateSlowGitChanged(bool value)
    {
        _settings.Update(s => s.SimulateSlowGit = value);
        _ = _settings.SaveAsync();
    }

    partial void OnDiffPrefetchConcurrencyChanged(int value)
    {
        var clamped = DiffWarmStore.ClampConcurrency(value);
        if (clamped != value)
        {
            DiffPrefetchConcurrency = clamped;
            return;
        }

        OnPropertyChanged(nameof(DiffPrefetchConcurrencyIndex));
        _settings.Update(s => s.DiffPrefetchConcurrency = clamped);
        _ = _settings.SaveAsync();
        WorkingCopy.SetDiffPrefetchConcurrency(clamped);
    }

    partial void OnDevelopmentFolderChanged(string value)
    {
        _settings.Update(s => s.DevelopmentFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        _ = _settings.SaveAsync();
        // Invalidate so the next flyout open / EnsureRepositoryCatalogAsync rescans.
        _catalogLoaded = false;
        _catalogRoot = null;
        NotifyRepositorySwitcherDisplayChanged();
    }

    partial void OnAiAssistanceEnabledChanged(bool value)
    {
        _settings.Update(s => s.AiAssistanceEnabled = value);
        _ = _settings.SaveAsync();
        Review.NotifyAiButtonStateChanged();
        WorkingCopy.NotifyAiCommitAssistVisibilityChanged();
    }

    partial void OnAiUseDedicatedCopilotTokenChanged(bool value)
    {
        _settings.Update(s => s.AiUseDedicatedCopilotToken = value);
        _ = _settings.SaveAsync();
    }

    partial void OnAiModelOverrideChanged(string value)
    {
        _settings.Update(s => s.AiModelOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        _ = _settings.SaveAsync();
    }

    partial void OnAiReasoningEffortChanged(string value)
    {
        _settings.Update(s => s.AiReasoningEffort = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        _ = _settings.SaveAsync();
    }

    partial void OnAiReviewRulesChanged(string value)
    {
        _settings.Update(s => s.AiReviewRules = value);
        _ = _settings.SaveAsync();
    }

    partial void OnAiTurnBudgetChanged(int value)
    {
        _settings.Update(s => s.AiTurnBudget = Math.Max(1, value));
        _ = _settings.SaveAsync();
    }

    partial void OnAiFileBriefingMinChangePercentChanged(int value)
    {
        _settings.Update(s => s.AiFileBriefingMinChangePercent = Math.Clamp(value, 0, 100));
        _ = _settings.SaveAsync();
    }

    partial void OnAiFileBriefingMinLinesChangedChanged(int value)
    {
        _settings.Update(s => s.AiFileBriefingMinLinesChanged = Math.Max(0, value));
        _ = _settings.SaveAsync();
    }

    partial void OnAiTurnTimeoutSecondsChanged(int value)
    {
        _settings.Update(s => s.AiTurnTimeoutSeconds = Math.Max(1, value));
        _ = _settings.SaveAsync();
    }

    partial void OnAiRunTimeoutSecondsChanged(int value)
    {
        _settings.Update(s => s.AiRunTimeoutSeconds = Math.Max(0, value));
        _ = _settings.SaveAsync();
    }

    partial void OnAiPathDenylistTextChanged(string value)
    {
        _settings.Update(s => s.AiPathDenylist = SplitLines(value));
        _ = _settings.SaveAsync();
    }

    partial void OnAiExcludedRepositoriesTextChanged(string value)
    {
        _settings.Update(s => s.AiExcludedRepositories = SplitLines(value));
        _ = _settings.SaveAsync();
        Review.NotifyAiButtonStateChanged();
    }

    partial void OnAiDisclosureAcknowledgedChanged(bool value)
    {
        _settings.Update(s => s.AiDisclosureAcknowledged = value);
        _ = _settings.SaveAsync();
    }

    partial void OnAiExportRetentionDaysChanged(int value)
    {
        _settings.Update(s => s.AiExportRetentionDays = Math.Max(1, value));
        _ = _settings.SaveAsync();
    }

    partial void OnAiLargePrFileThresholdChanged(int value)
    {
        _settings.Update(s => s.AiLargePrFileThreshold = Math.Max(1, value));
        _ = _settings.SaveAsync();
    }

    partial void OnTicketFromBranchRegexChanged(string value)
    {
        _settings.Update(s => s.TicketFromBranchRegex =
            string.IsNullOrWhiteSpace(value) ? TicketFromBranch.DefaultRegex : value);
        _ = _settings.SaveAsync();
    }

    private static List<string> SplitLines(string value) =>
        value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    [RelayCommand]
    private async Task TestAiConnectionAsync()
    {
        IsTestingAiConnection = true;
        AiConnectionTestResult = null;
        try
        {
            var result = await _ai.TestConnectionAsync().ConfigureAwait(true);
            AiConnectionTestResult = result.Succeeded
                ? $"Connected — {result.Message}"
                : $"Failed — {result.Message}";
        }
        catch (Exception ex)
        {
            AiConnectionTestResult = $"Failed — {ex.Message}";
        }
        finally
        {
            IsTestingAiConnection = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAiModelsAsync()
    {
        IsRefreshingAiModels = true;
        try
        {
            var models = await _ai.ListModelsAsync().ConfigureAwait(true);
            AiAvailableModels.Clear();
            foreach (var model in models)
                AiAvailableModels.Add(model);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to load AI models: {ex.Message}", exception: ex);
        }
        finally
        {
            IsRefreshingAiModels = false;
        }
    }

    [RelayCommand]
    private async Task ClearAiDataAsync()
    {
        var confirmed = await _confirm.ConfirmAsync(
                "Clear AI data?",
                "This deletes cached AI triage results, file summaries, annotations, and chat history " +
                "for all pull requests. This cannot be undone.",
                "Clear")
            .ConfigureAwait(true);
        if (!confirmed) return;

        IsClearingAiData = true;
        try
        {
            await _ai.ClearAiDataAsync().ConfigureAwait(true);
            _notifications.Info("AI data cleared.");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to clear AI data: {ex.Message}", exception: ex);
        }
        finally
        {
            IsClearingAiData = false;
        }
    }

    [RelayCommand]
    private async Task AddGitHubAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(NewGitHubToken))
        {
            _notifications.Error("Paste a personal access token to add an account.");
            return;
        }

        try
        {
            var account = await _accounts.AddAccountAsync(NewGitHubHost, NewGitHubToken);
            RefreshAccountCollections();
            NewGitHubToken = "";
            IsAddingGitHubAccount = false;
            SelectedGitHubAccount = GitHubAccounts.FirstOrDefault(a =>
                string.Equals(a.Host, account.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Login, account.Login, StringComparison.OrdinalIgnoreCase));
            _notifications.Info($"Added GitHub account {account.Login} on {account.Host}.");
            await Review.RefreshInboxCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to add GitHub account: {ex.Message}",
                () => _ = AddGitHubAccountAsync(), ex);
        }
    }

    [RelayCommand]
    private async Task ReauthGitHubAccountAsync(GitHubAccountSettings? account)
    {
        if (account is null) return;

        ReauthAccount = account;
        if (string.IsNullOrWhiteSpace(ReauthToken))
        {
            _notifications.Error("Paste a new token to re-authenticate.");
            return;
        }

        try
        {
            await _accounts.ReauthAccountAsync(account.Host, account.Login, ReauthToken);
            RefreshAccountCollections();
            ReauthToken = "";
            ReauthAccount = null;
            _notifications.Info($"Re-authenticated {account.Login} on {account.Host}.");
            await Review.RefreshInboxCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to re-authenticate: {ex.Message}",
                () => _ = ReauthGitHubAccountAsync(account), ex);
        }
    }

    [RelayCommand]
    private void BeginReauth(GitHubAccountSettings? account)
    {
        ReauthAccount = account;
        ReauthToken = "";
    }

    [RelayCommand]
    private void AddEnterpriseHost()
    {
        var host = NewEnterpriseHost.Trim();
        if (string.IsNullOrWhiteSpace(host))
            return;

        if (EnterpriseHostUrls.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
        {
            NewEnterpriseHost = "";
            return;
        }

        EnterpriseHostUrls.Add(host);
        NewEnterpriseHost = "";
        _settings.Update(s =>
        {
            if (!s.EnterpriseHostUrls.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
                s.EnterpriseHostUrls.Add(host);
        });
        _ = _settings.SaveAsync();
    }

    [RelayCommand]
    private void RemoveEnterpriseHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;

        EnterpriseHostUrls.Remove(host);
        _settings.Update(s =>
            s.EnterpriseHostUrls.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)));
        _ = _settings.SaveAsync();
    }

    private void RefreshAccountCollections()
    {
        var selectedHost = SelectedGitHubAccount?.Host;
        var selectedLogin = SelectedGitHubAccount?.Login;

        GitHubAccounts.Clear();
        foreach (var existing in _accounts.ListAccounts())
            GitHubAccounts.Add(existing);

        OnPropertyChanged(nameof(HasGitHubAccounts));

        if (selectedHost is not null && selectedLogin is not null)
        {
            SelectedGitHubAccount = GitHubAccounts.FirstOrDefault(a =>
                string.Equals(a.Host, selectedHost, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Login, selectedLogin, StringComparison.OrdinalIgnoreCase));
        }
    }

    [RelayCommand]
    private async Task RemoveGitHubAccountAsync(GitHubAccountSettings? account)
    {
        if (account is null) return;

        try
        {
            await _accounts.RemoveAccountAsync(account.Host, account.Login);
            RefreshAccountCollections();
            if (GitHubAccounts.Count > 0)
            {
                IsAddingGitHubAccount = false;
                SelectedGitHubAccount = GitHubAccounts[0];
            }
            else
            {
                BeginAddGitHubAccount();
            }
            await Review.RefreshInboxCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to remove GitHub account: {ex.Message}",
                () => _ = RemoveGitHubAccountAsync(account), ex);
        }
    }

    public void NotifyWindowActivated()
    {
        WorkingCopy.NotifyWindowActivated();
        Review.NotifyWindowActivated();
    }

    [RelayCommand]
    private void ReturnToFileStatus()
    {
        Review.ClearPullRequestMode();
        WorkingCopy.SelectFileStatusCommand.Execute(null);
    }

    [RelayCommand]
    private void SelectWorkspaceFileStatus()
    {
        Review.ClearPullRequestMode();
        WorkingCopy.SelectFileStatusCommand.Execute(null);
    }

    [RelayCommand]
    private void SelectWorkspaceHistory()
    {
        Review.ClearPullRequestMode();
        WorkingCopy.SelectHistoryCommand.Execute(null);
    }
}

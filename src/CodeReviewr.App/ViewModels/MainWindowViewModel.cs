using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.GitHub;

namespace CodeReviewr.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const double CollapsedNavigatorWidth = 36;
    private const double MinNavigatorWidth = 160;
    private const double MinFileListWidth = 200;

    private readonly ISettingsStore _settings;
    private readonly NotificationService _notifications;
    private readonly IAccountService _accounts;
    private double _expandedNavigatorWidth;

    public MainWindowViewModel(
        WorkingCopyViewModel workingCopy,
        ReviewViewModel review,
        DiagnosticsOverlayViewModel diagnostics,
        GitConsoleViewModel gitConsole,
        ISettingsStore settings,
        NotificationService notifications,
        IAccountService accounts)
    {
        WorkingCopy = workingCopy;
        Review = review;
        Diagnostics = diagnostics;
        GitConsole = gitConsole;
        _settings = settings;
        _notifications = notifications;
        _accounts = accounts;
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
            }
        };
        Review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReviewViewModel.IsPullRequestMode)
                or nameof(ReviewViewModel.WorkspaceMode))
            {
                OnPropertyChanged(nameof(ShowFileStatusPane));
                OnPropertyChanged(nameof(ShowPullRequestPane));
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
    }

    public WorkingCopyViewModel WorkingCopy { get; }
    public ReviewViewModel Review { get; }
    public DiagnosticsOverlayViewModel Diagnostics { get; }
    public GitConsoleViewModel GitConsole { get; }
    public NotificationService Notifications => _notifications;

    public System.Collections.ObjectModel.ObservableCollection<GitHubAccountSettings> GitHubAccounts { get; }
    public System.Collections.ObjectModel.ObservableCollection<string> EnterpriseHostUrls { get; }

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

    public System.Collections.ObjectModel.ObservableCollection<string> RecentRepositories { get; }

    public bool HasRecentRepositories => RecentRepositories.Count > 0;

    public GridLength NavigatorColumnWidth => new(NavigatorWidth);
    public GridLength FileListColumnWidth => new(FileListWidth);
    public bool ShowFileStatusPane => !WorkingCopy.IsHistoryMode && !Review.IsPullRequestMode;
    public bool ShowPullRequestPane => Review.IsPullRequestMode;

    partial void OnNavigatorWidthChanged(double value) => OnPropertyChanged(nameof(NavigatorColumnWidth));
    partial void OnFileListWidthChanged(double value) => OnPropertyChanged(nameof(FileListColumnWidth));

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
        try
        {
            StatusText = $"Opening {path}…";
            await WorkingCopy.OpenAsync(path);
            AddRecent(path);
            StatusText = WorkingCopy.CurrentBranch is null
                ? path
                : $"{WorkingCopy.CurrentBranch} — {path}";
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to open repository: {ex.Message}", () => _ = OpenRepositoryPathAsync(path));
            StatusText = "Failed to open repository";
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
    private void OpenSettings() => ShowSettings = true;

    [RelayCommand]
    private void CloseSettings() => ShowSettings = false;

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
        global::CodeReviewr.App.App.ApplyTheme(value);
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
            _notifications.Info($"Added GitHub account {account.Login} on {account.Host}.");
            await Review.RefreshInboxCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to add GitHub account: {ex.Message}",
                () => _ = AddGitHubAccountAsync());
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
                () => _ = ReauthGitHubAccountAsync(account));
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
        GitHubAccounts.Clear();
        foreach (var existing in _accounts.ListAccounts())
            GitHubAccounts.Add(existing);
    }

    [RelayCommand]
    private async Task RemoveGitHubAccountAsync(GitHubAccountSettings? account)
    {
        if (account is null) return;

        try
        {
            await _accounts.RemoveAccountAsync(account.Host, account.Login);
            RefreshAccountCollections();
            await Review.RefreshInboxCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to remove GitHub account: {ex.Message}",
                () => _ = RemoveGitHubAccountAsync(account));
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
}

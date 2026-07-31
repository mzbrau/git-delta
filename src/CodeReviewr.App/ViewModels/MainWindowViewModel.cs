using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const double CollapsedNavigatorWidth = 36;
    private const double MinNavigatorWidth = 160;
    private const double MinFileListWidth = 200;

    private readonly ISettingsStore _settings;
    private readonly NotificationService _notifications;
    private double _expandedNavigatorWidth;

    public MainWindowViewModel(
        WorkingCopyViewModel workingCopy,
        DiagnosticsOverlayViewModel diagnostics,
        GitConsoleViewModel gitConsole,
        ISettingsStore settings,
        NotificationService notifications)
    {
        WorkingCopy = workingCopy;
        Diagnostics = diagnostics;
        GitConsole = gitConsole;
        _settings = settings;
        _notifications = notifications;
        RecentRepositories = new(_settings.Current.RecentRepositories);
        DefaultDiffMode = _settings.Current.DefaultDiffMode;
        _theme = string.IsNullOrWhiteSpace(_settings.Current.Theme) ? "System" : _settings.Current.Theme;

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
    public DiagnosticsOverlayViewModel Diagnostics { get; }
    public GitConsoleViewModel GitConsole { get; }
    public NotificationService Notifications => _notifications;

    [ObservableProperty] private GitExecutableInfo? _gitInfo;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private DiffViewMode _defaultDiffMode;
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private double _navigatorWidth;
    [ObservableProperty] private double _fileListWidth;
    [ObservableProperty] private bool _isNavigatorCollapsed;
    [ObservableProperty] private double _windowWidth;
    [ObservableProperty] private double _windowHeight;

    public System.Collections.ObjectModel.ObservableCollection<string> RecentRepositories { get; }

    public bool HasRecentRepositories => RecentRepositories.Count > 0;

    public GridLength NavigatorColumnWidth => new(NavigatorWidth);
    public GridLength FileListColumnWidth => new(FileListWidth);

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
}

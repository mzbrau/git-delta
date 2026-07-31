using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly NotificationService _notifications;

    public MainWindowViewModel(
        WorkingCopyViewModel workingCopy,
        DiagnosticsOverlayViewModel diagnostics,
        ISettingsStore settings,
        NotificationService notifications)
    {
        WorkingCopy = workingCopy;
        Diagnostics = diagnostics;
        _settings = settings;
        _notifications = notifications;
        RecentRepositories = new(_settings.Current.RecentRepositories);
        DefaultDiffMode = _settings.Current.DefaultDiffMode;
    }

    public WorkingCopyViewModel WorkingCopy { get; }
    public DiagnosticsOverlayViewModel Diagnostics { get; }
    public NotificationService Notifications => _notifications;

    [ObservableProperty] private GitExecutableInfo? _gitInfo;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private DiffViewMode _defaultDiffMode;

    public System.Collections.ObjectModel.ObservableCollection<string> RecentRepositories { get; }

    public bool HasRecentRepositories => RecentRepositories.Count > 0;

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
}

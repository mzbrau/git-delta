using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using GitDelta.Diff;

namespace GitDelta.App.ViewModels;

/// <summary>
/// Shared file-history browse state and load/cancel logic for WC pending review and PR review.
/// </summary>
public sealed partial class FileHistoryBrowseController : ObservableObject
{
    private readonly IGitHistoryService _history;
    private readonly IGitDiffService _diff;
    private readonly Func<IFileHistoryBrowseHost?> _hostFactory;
    private CancellationTokenSource? _cts;
    private readonly List<BrowseFrame> _backStack = [];
    private FilePath? _currentPath;
    private int _pathNavigationDepth;

    public FileHistoryBrowseController(
        IGitHistoryService history,
        IGitDiffService diff,
        Func<IFileHistoryBrowseHost?> hostFactory)
    {
        _history = history;
        _diff = diff;
        _hostFactory = hostFactory;
    }

    [ObservableProperty] private bool _isFileHistoryBrowseMode;
    [ObservableProperty] private FileHistoryCompareMode _compareMode = FileHistoryCompareMode.InCommit;
    [ObservableProperty] private string? _selectedOid;
    [ObservableProperty] private string? _selectedShortOid;
    [ObservableProperty] private string? _selectedSubject;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _breadcrumbTrail = "";
    [ObservableProperty] private bool _canNavigateBack;

    public bool CanExitFileHistoryBrowseMode => IsFileHistoryBrowseMode;

    /// <summary>
    /// True while the controller is changing the browse subject path (sibling file / back).
    /// Hosts use this to avoid treating the selection change as an exit from browse mode.
    /// </summary>
    public bool IsPathNavigationInProgress => _pathNavigationDepth > 0;

    public bool IsCompareInCommit
    {
        get => CompareMode == FileHistoryCompareMode.InCommit;
        set
        {
            if (value)
                CompareMode = FileHistoryCompareMode.InCommit;
        }
    }

    public bool IsCompareVsCurrent
    {
        get => CompareMode == FileHistoryCompareMode.VsCurrent;
        set
        {
            if (value)
                CompareMode = FileHistoryCompareMode.VsCurrent;
        }
    }

    partial void OnIsFileHistoryBrowseModeChanged(bool value) =>
        OnPropertyChanged(nameof(CanExitFileHistoryBrowseMode));

    partial void OnCompareModeChanged(FileHistoryCompareMode value)
    {
        OnPropertyChanged(nameof(IsCompareInCommit));
        OnPropertyChanged(nameof(IsCompareVsCurrent));
        if (IsFileHistoryBrowseMode && !string.IsNullOrEmpty(SelectedOid))
            _ = ReloadAsync();
    }

    public void Reset()
    {
        _cts?.Cancel();
        _cts = null;
        ClearNavigationStack();
        IsFileHistoryBrowseMode = false;
        CompareMode = FileHistoryCompareMode.InCommit;
        SelectedOid = null;
        SelectedShortOid = null;
        SelectedSubject = null;
        IsLoading = false;
        _currentPath = null;
        RefreshBreadcrumb();
    }

    public void ClearSelectionHighlight(FileHistoryCacheEntry? cache)
    {
        cache?.ClearSelection();
    }

    [RelayCommand]
    private async Task ExitFileHistoryBrowseAsync()
    {
        var host = _hostFactory();
        if (host is null)
            return;

        _cts?.Cancel();
        ClearNavigationStack();
        IsFileHistoryBrowseMode = false;
        SelectedOid = null;
        SelectedShortOid = null;
        SelectedSubject = null;
        _currentPath = null;
        RefreshBreadcrumb();
        await host.ExitFileHistoryBrowseAsync().ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateBack))]
    private async Task NavigateBackAsync()
    {
        if (_backStack.Count == 0)
            return;

        var host = _hostFactory();
        if (host is null)
            return;

        var frame = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        SelectedOid = frame.Oid;
        SelectedShortOid = frame.ShortOid;
        SelectedSubject = frame.Subject;
        _currentPath = frame.Path;
        RefreshBreadcrumb();

        BeginPathNavigation();
        try
        {
            IsFileHistoryBrowseMode = true;
            await host.OpenPathInFileHistoryBrowseAsync(frame.Path, frame.Oid, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            EndPathNavigation();
        }
    }

    public async Task SelectHistoryItemAsync(
        FileHistoryItemViewModel item,
        FileHistoryCacheEntry? cache,
        CancellationToken ct = default)
    {
        var host = _hostFactory();
        if (host is null)
            return;

        if (item.IsCurrent)
        {
            cache?.ClearSelection();
            await ExitFileHistoryBrowseAsync().ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(item.Oid))
            return;

        ClearNavigationStack();
        cache?.ClearSelection();
        item.IsSelected = true;
        SelectedOid = item.Oid;
        SelectedShortOid = item.ShortOid;
        SelectedSubject = item.Subject;
        _currentPath = host.BrowseSubjectPath;
        IsFileHistoryBrowseMode = true;
        RefreshBreadcrumb();
        await LoadBrowseDiffAsync(host, item.Oid, host.BrowseSubjectPath, ct).ConfigureAwait(false);
    }

    public async Task SelectCommitFileAsync(
        FileHistoryItemViewModel item,
        FileHistoryCommitFileItem file,
        FileHistoryCacheEntry? cache,
        CancellationToken ct = default)
    {
        var host = _hostFactory();
        if (host is null || string.IsNullOrEmpty(item.Oid))
            return;

        var nextPath = file.Path;
        var current = _currentPath ?? host.BrowseSubjectPath;
        if (current is { } from
            && !string.Equals(from.Value, nextPath.Value, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(item.Oid))
        {
            _backStack.Add(new BrowseFrame(from, item.Oid, item.ShortOid, item.Subject));
        }

        cache?.ClearSelection();
        item.IsSelected = true;
        SelectedOid = item.Oid;
        SelectedShortOid = item.ShortOid;
        SelectedSubject = item.Subject;
        _currentPath = nextPath;
        IsFileHistoryBrowseMode = true;
        RefreshBreadcrumb();

        BeginPathNavigation();
        try
        {
            await host.OpenPathInFileHistoryBrowseAsync(nextPath, item.Oid, ct).ConfigureAwait(false);
        }
        finally
        {
            EndPathNavigation();
        }
    }

    public async Task EnsureCommitFilesAsync(FileHistoryItemViewModel item, CancellationToken ct = default)
    {
        if (!item.CanExpand || item.IsFilesReady || item.IsFilesLoading)
            return;

        var host = _hostFactory();
        var repo = host?.RepositoryPath;
        if (string.IsNullOrWhiteSpace(repo))
            return;

        item.FilesState = FileHistoryCommitFilesLoadState.Loading;
        try
        {
            var files = await _history.GetCommitFilesAsync(repo, item.Oid, ct).ConfigureAwait(false);
            item.ApplyFiles(files);
        }
        catch (OperationCanceledException)
        {
            item.FilesState = FileHistoryCommitFilesLoadState.NotLoaded;
        }
        catch (Exception ex)
        {
            item.ApplyFilesFailure(ex.Message);
        }
    }

    public async Task ToggleExpandedAsync(FileHistoryItemViewModel item, CancellationToken ct = default)
    {
        if (!item.CanExpand)
            return;

        item.IsExpanded = !item.IsExpanded;
        if (item.IsExpanded)
            await EnsureCommitFilesAsync(item, ct).ConfigureAwait(false);
    }

    /// <summary>Reloads the active browse diff after the host changed the subject path.</summary>
    public async Task ReloadForPathAsync(FilePath path, string oid, CancellationToken ct = default)
    {
        var host = _hostFactory();
        if (host is null)
            return;

        SelectedOid = oid;
        _currentPath = path;
        IsFileHistoryBrowseMode = true;
        RefreshBreadcrumb();
        await LoadBrowseDiffAsync(host, oid, path, ct).ConfigureAwait(false);
    }

    private void BeginPathNavigation() => _pathNavigationDepth++;

    private void EndPathNavigation()
    {
        if (_pathNavigationDepth > 0)
            _pathNavigationDepth--;
    }

    private void ClearNavigationStack()
    {
        _backStack.Clear();
        RefreshBreadcrumb();
    }

    private void RefreshBreadcrumb()
    {
        CanNavigateBack = _backStack.Count > 0;
        NavigateBackCommand.NotifyCanExecuteChanged();

        if (!IsFileHistoryBrowseMode && _backStack.Count == 0 && _currentPath is null)
        {
            BreadcrumbTrail = "";
            return;
        }

        var parts = new List<string>(_backStack.Count + 1);
        foreach (var frame in _backStack)
            parts.Add(DisplayName(frame.Path));
        if (_currentPath is { } current)
            parts.Add(DisplayName(current));

        BreadcrumbTrail = parts.Count == 0 ? "" : string.Join(" › ", parts);
    }

    private static string DisplayName(FilePath path)
    {
        var value = path.Value;
        var slash = value.LastIndexOf('/');
        return slash >= 0 && slash < value.Length - 1 ? value[(slash + 1)..] : value;
    }

    private Task ReloadAsync() =>
        SelectedOid is { } oid
            ? LoadBrowseDiffAsync(_hostFactory(), oid, _currentPath ?? _hostFactory()?.BrowseSubjectPath, CancellationToken.None)
            : Task.CompletedTask;

    private async Task LoadBrowseDiffAsync(
        IFileHistoryBrowseHost? host,
        string oid,
        FilePath? path,
        CancellationToken ct)
    {
        if (host is null || path is null || string.IsNullOrWhiteSpace(host.RepositoryPath))
            return;

        var subject = path.Value;
        _cts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        var token = cts.Token;

        IsLoading = true;
        try
        {
            var options = host.BuildDiffOptions();
            FileDiff diff;
            if (CompareMode == FileHistoryCompareMode.InCommit)
            {
                var raw = await _history.GetCommitPatchAsync(host.RepositoryPath, oid, subject, options, token)
                    .ConfigureAwait(false);
                diff = string.IsNullOrWhiteSpace(raw)
                    ? CleanFileDiff.Create(subject, Array.Empty<byte>(), new DiffScope.RevisionToWorktree(CommitId.FromSha(oid)))
                    : PatchParser.Parse(raw, new DiffScope.RevisionToWorktree(CommitId.FromSha(oid)));
            }
            else if (host.CurrentRevision is { } head)
            {
                var scope = new DiffScope.RevisionsTwoDot(CommitId.FromSha(oid), head);
                diff = await _diff.GetDiffAsync(host.RepositoryPath, subject, scope, options, token)
                    .ConfigureAwait(false);
            }
            else
            {
                var scope = new DiffScope.RevisionToWorktree(CommitId.FromSha(oid));
                diff = await _diff.GetDiffAsync(host.RepositoryPath, subject, scope, options, token)
                    .ConfigureAwait(false);
            }

            if (!ReferenceEquals(_cts, cts))
                return;

            await host.PresentFileHistoryDiffAsync(subject, diff, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception)
        {
            if (!ReferenceEquals(_cts, cts))
                return;
            throw;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsLoading = false;
                cts.Dispose();
                if (ReferenceEquals(_cts, cts))
                    _cts = null;
            }
        }
    }

    private readonly record struct BrowseFrame(FilePath Path, string Oid, string? ShortOid, string? Subject);
}

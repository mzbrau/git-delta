using System.Collections.ObjectModel;
using GitDelta.Core;
using GitDelta.Core.Diff;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitDelta.App.ViewModels;

public partial class WorkingCopyViewModel
{
    private const int ContentSearchDebounceMs = 180;

    private CancellationTokenSource? _fileStatusSearchCts;
    private CancellationTokenSource? _historySearchCts;
    private readonly Dictionary<string, bool> _fileStatusSearchExpandState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _historySearchExpandState = new(StringComparer.Ordinal);
    private List<(FileItemViewModel File, IReadOnlyList<ChangedLineSearch.Hit> Hits)> _stagedSearchResults = [];
    private List<(FileItemViewModel File, IReadOnlyList<ChangedLineSearch.Hit> Hits)> _unstagedSearchResults = [];
    private List<(FileItemViewModel File, IReadOnlyList<ChangedLineSearch.Hit> Hits)> _conflictedSearchResults = [];
    private List<(FileItemViewModel File, IReadOnlyList<ChangedLineSearch.Hit> Hits)> _historySearchResults = [];
    private (DiffSide Side, int Line)? _pendingDiffScroll;

    /// <summary>Raised on the UI thread after a diff load when a search hit requested scroll.</summary>
    public event Action<DiffSide, int>? DiffScrollRequested;

    [ObservableProperty] private FileListQueryMode _fileStatusQueryMode = FileListQueryMode.Filter;
    [ObservableProperty] private FileListQueryMode _historyFileQueryMode = FileListQueryMode.Filter;

    public bool IsFileStatusFilterMode => FileStatusQueryMode == FileListQueryMode.Filter;
    public bool IsFileStatusSearchMode => FileStatusQueryMode == FileListQueryMode.Search;
    public bool IsHistoryFileFilterMode => HistoryFileQueryMode == FileListQueryMode.Filter;
    public bool IsHistoryFileSearchMode => HistoryFileQueryMode == FileListQueryMode.Search;

    public bool IsFileStatusContentSearchActive =>
        IsFileStatusSearchMode && !string.IsNullOrWhiteSpace(FileFilter);

    public bool IsHistoryContentSearchActive =>
        IsHistoryFileSearchMode && !string.IsNullOrWhiteSpace(HistoryFileFilter);

    public string FileStatusQueryPlaceholder =>
        IsFileStatusSearchMode ? "Search changes…" : "Filter files…";

    public string HistoryFileQueryPlaceholder =>
        IsHistoryFileSearchMode ? "Search changes…" : "Filter files…";

    public string FileStatusQueryClearTip =>
        IsFileStatusSearchMode ? "Clear search" : "Clear filter";

    public string HistoryFileQueryClearTip =>
        IsHistoryFileSearchMode ? "Clear search" : "Clear filter";

    public Material.Icons.MaterialIconKind FileStatusQueryModeIcon =>
        IsFileStatusSearchMode
            ? Material.Icons.MaterialIconKind.Magnify
            : Material.Icons.MaterialIconKind.FilterVariant;

    public Material.Icons.MaterialIconKind HistoryFileQueryModeIcon =>
        IsHistoryFileSearchMode
            ? Material.Icons.MaterialIconKind.Magnify
            : Material.Icons.MaterialIconKind.FilterVariant;

    partial void OnFileStatusQueryModeChanged(FileListQueryMode value)
    {
        NotifyFileStatusQueryModeChrome();
        if (IsStashMode && SelectedStash is not null)
            _ = SelectStashAsync(SelectedStash);
        else
            ApplyFileFilter();
    }

    partial void OnHistoryFileQueryModeChanged(FileListQueryMode value)
    {
        NotifyHistoryFileQueryModeChrome();
        ApplyHistoryFileFilter();
    }

    private void NotifyFileStatusQueryModeChrome()
    {
        OnPropertyChanged(nameof(IsFileStatusFilterMode));
        OnPropertyChanged(nameof(IsFileStatusSearchMode));
        OnPropertyChanged(nameof(IsFileStatusContentSearchActive));
        OnPropertyChanged(nameof(FileStatusQueryPlaceholder));
        OnPropertyChanged(nameof(FileStatusQueryClearTip));
        OnPropertyChanged(nameof(FileStatusQueryModeIcon));
    }

    private void NotifyHistoryFileQueryModeChrome()
    {
        OnPropertyChanged(nameof(IsHistoryFileFilterMode));
        OnPropertyChanged(nameof(IsHistoryFileSearchMode));
        OnPropertyChanged(nameof(IsHistoryContentSearchActive));
        OnPropertyChanged(nameof(HistoryFileQueryPlaceholder));
        OnPropertyChanged(nameof(HistoryFileQueryClearTip));
        OnPropertyChanged(nameof(HistoryFileQueryModeIcon));
    }

    [RelayCommand]
    private void SetFileStatusQueryMode(FileListQueryMode mode) => FileStatusQueryMode = mode;

    [RelayCommand]
    private void SetHistoryFileQueryMode(FileListQueryMode mode) => HistoryFileQueryMode = mode;

    /// <summary>Select a search hit: open the file and scroll to the matching line after load.</summary>
    public void SelectSearchHit(FileItemViewModel file, DiffSide side, int line)
    {
        _pendingDiffScroll = (side, line);
        var alreadySelected = SameSelectionIdentity(SelectedFile, file);
        SetFileSelection([file]);

        // Same-path selection skips PresentDiff, so drain the pending scroll here (or force a reload).
        if (alreadySelected)
        {
            if (DiffRows.Count > 0)
                RequestPendingDiffScrollIfAny();
            else
                _ = LoadDiffForSelectionAsync(file);
        }
    }

    private void RequestPendingDiffScrollIfAny()
    {
        if (_pendingDiffScroll is not { } pending)
            return;

        _pendingDiffScroll = null;
        DiffScrollRequested?.Invoke(pending.Side, pending.Line);
    }

    private void ScheduleFileStatusContentSearch()
    {
        _fileStatusSearchCts?.Cancel();
        _fileStatusSearchCts = new CancellationTokenSource();
        var ct = _fileStatusSearchCts.Token;
        _ = RunFileStatusContentSearchAsync(FileFilter, ct);
    }

    private void ScheduleHistoryContentSearch()
    {
        _historySearchCts?.Cancel();
        _historySearchCts = new CancellationTokenSource();
        var ct = _historySearchCts.Token;
        _ = RunHistoryContentSearchAsync(HistoryFileFilter, ct);
    }

    private async Task RunFileStatusContentSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ContentSearchDebounceMs, ct).ConfigureAwait(false);
            if (_repoPath is null) return;

            var staged = _allStaged.ToList();
            var unstaged = _allUnstaged.ToList();
            var conflicted = _allConflicted.ToList();
            var options = BuildDiffOptions();

            var stagedResults = await SearchFilesAsync(staged, query, options, ct).ConfigureAwait(false);
            var unstagedResults = await SearchFilesAsync(unstaged, query, options, ct).ConfigureAwait(false);
            var conflictedResults = await SearchFilesAsync(conflicted, query, options, ct).ConfigureAwait(false);

            await InvokeOnUiAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                if (!IsFileStatusContentSearchActive) return;
                if (!string.Equals(FileFilter.Trim(), query.Trim(), StringComparison.Ordinal))
                    return;

                _stagedSearchResults = stagedResults;
                _unstagedSearchResults = unstagedResults;
                _conflictedSearchResults = conflictedResults;
                ApplyFileStatusSearchResults();
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
    }

    private async Task RunHistoryContentSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ContentSearchDebounceMs, ct).ConfigureAwait(false);
            if (_repoPath is null || SelectedCommit is null) return;

            var files = _allHistoryFiles.ToList();
            var oid = SelectedCommit.Oid;
            var options = BuildDiffOptions();
            var results = await SearchHistoryFilesAsync(files, oid, query, options, ct).ConfigureAwait(false);

            await InvokeOnUiAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                if (!IsHistoryContentSearchActive) return;
                if (!string.Equals(HistoryFileFilter.Trim(), query.Trim(), StringComparison.Ordinal))
                    return;

                _historySearchResults = results;
                ApplyHistorySearchResults();
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
    }

    private async Task<List<(FileItemViewModel File, IReadOnlyList<ChangedLineSearch.Hit> Hits)>> SearchFilesAsync(
        IReadOnlyList<FileItemViewModel> files,
        string query,
        DiffOptions options,
        CancellationToken ct)
    {
        var results = new List<(FileItemViewModel, IReadOnlyList<ChangedLineSearch.Hit>)>();
        if (files.Count == 0 || _repoPath is null)
            return results;

        var repoPath = _repoPath;
        var gate = new SemaphoreSlim(_warmStore.MaxConcurrencyLimit, _warmStore.MaxConcurrencyLimit);
        var tasks = files.Select(async file =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var diff = await ResolveFileStatusDiffAsync(repoPath, file, options, ct).ConfigureAwait(false);
                var hits = ChangedLineSearch.FindHits(diff, query);
                return (file, hits);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (file, (IReadOnlyList<ChangedLineSearch.Hit>)[]);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (file, hits) in completed)
        {
            if (hits.Count > 0)
                results.Add((file, hits));
        }

        return results;
    }

    private async Task<List<(FileItemViewModel File, IReadOnlyList<ChangedLineSearch.Hit> Hits)>> SearchHistoryFilesAsync(
        IReadOnlyList<FileItemViewModel> files,
        string oid,
        string query,
        DiffOptions options,
        CancellationToken ct)
    {
        var results = new List<(FileItemViewModel, IReadOnlyList<ChangedLineSearch.Hit>)>();
        if (files.Count == 0 || _repoPath is null)
            return results;

        var repoPath = _repoPath;
        var gate = new SemaphoreSlim(_warmStore.MaxConcurrencyLimit, _warmStore.MaxConcurrencyLimit);
        var tasks = files.Select(async file =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var key = HistoryWarmKey(oid, file.Path, options);
                var diff = await _warmStore.GetOrStart(
                    key,
                    token => LoadHistoryFileDiffAsync(repoPath, oid, file.Path, file.Kind, options, token)).WaitAsync(ct)
                    .ConfigureAwait(false);
                var hits = ChangedLineSearch.FindHits(diff, query);
                return (file, hits);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (file, (IReadOnlyList<ChangedLineSearch.Hit>)[]);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (file, hits) in completed)
        {
            if (hits.Count > 0)
                results.Add((file, hits));
        }

        return results;
    }

    private async Task<FileDiff> ResolveFileStatusDiffAsync(
        string repoPath,
        FileItemViewModel file,
        DiffOptions options,
        CancellationToken ct)
    {
        var target = IsCombinedReviewMode
            ? DiffTarget.HeadToWorktree
            : file.IsStagedList ? DiffTarget.HeadToIndex : DiffTarget.IndexToWorktree;
        var key = FileStatusWarmKey(file.Path, target, options);

        if (file.Kind == ChangeKind.Untracked)
        {
            return await _warmStore.GetOrStart(
                key,
                token => LoadUntrackedFileDiffAsync(repoPath, file.Path, target, token)).WaitAsync(ct)
                .ConfigureAwait(false);
        }

        return await _warmStore.GetOrStart(
            key,
            token => _diffService.GetDiffAsync(repoPath, file.Path, target.AsWorkingCopy(), options, token)).WaitAsync(ct)
            .ConfigureAwait(false);
    }

    private void ApplyFileStatusSearchResults()
    {
        var previousKeys = _selectedFiles
            .Select(f => (Path: f.Path.Value, f.IsStagedList))
            .ToList();

        _suppressSelectionSync = true;
        try
        {
            StagedFiles.Clear();
            UnstagedFiles.Clear();
            ConflictedFiles.Clear();

            foreach (var (file, _) in _stagedSearchResults)
                StagedFiles.Add(file);
            foreach (var (file, _) in _unstagedSearchResults)
                UnstagedFiles.Add(file);
            foreach (var (file, _) in _conflictedSearchResults)
                ConflictedFiles.Add(file);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        RebuildFileStatusEntries();

        OnPropertyChanged(nameof(HasConflictedFiles));
        OnPropertyChanged(nameof(HasStagedFiles));
        OnPropertyChanged(nameof(StagedFileCount));
        OnPropertyChanged(nameof(UnstagedFileCount));
        OnPropertyChanged(nameof(ShowCommitDock));
        NotifyCanCommitChanged();

        if (previousKeys.Count == 0) return;

        var all = StagedFiles.Concat(UnstagedFiles).Concat(ConflictedFiles).ToList();
        var restored = new List<FileItemViewModel>();
        foreach (var key in previousKeys)
        {
            var match = all.FirstOrDefault(f =>
                string.Equals(f.Path.Value, key.Path, StringComparison.Ordinal)
                && f.IsStagedList == key.IsStagedList);
            if (match is null)
            {
                match = all.FirstOrDefault(f =>
                    string.Equals(f.Path.Value, key.Path, StringComparison.Ordinal));
            }

            if (match is not null && restored.All(r => !ReferenceEquals(r, match)))
                restored.Add(match);
        }

        ApplySelectionState(restored, requestViewSync: true);
    }

    private void ApplyHistorySearchResults(bool autoSelectFirst = false)
    {
        if (!IsHistoryMode) return;

        var previousPath = _selectedFiles.FirstOrDefault()?.Path.Value
                           ?? SelectedFile?.Path.Value;

        _suppressSelectionSync = true;
        try
        {
            HistoryFiles.Clear();
            foreach (var (file, _) in _historySearchResults)
                HistoryFiles.Add(file);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        RebuildHistoryFileEntries();

        if (HistoryFiles.Count == 0)
        {
            if (_selectedFiles.Count > 0 || SelectedFile is not null)
            {
                _selectedFiles.Clear();
                SelectedFileCount = 0;
                SelectedFile = null;
                DiffRows.Clear();
                _currentDiff = null;
                ClearImagePreview();
                DiffEmptyMessage = "No matching changes";
            }

            return;
        }

        FileItemViewModel? restore = null;
        if (previousPath is not null)
        {
            restore = HistoryFiles.FirstOrDefault(f =>
                string.Equals(f.Path.Value, previousPath, StringComparison.Ordinal));
        }

        if (restore is null && autoSelectFirst)
            restore = HistoryFiles[0];

        if (restore is not null)
            ApplySelectionState([restore], requestViewSync: true);
    }

    private void RebuildFileStatusSearchEntries()
    {
        SelectionClearRequested?.Invoke();
        FileListLayoutHelper.RebuildSearchResults(
            StagedFileEntries, _stagedSearchResults, flatUsesFullPath: false, _fileStatusSearchExpandState);
        FileListLayoutHelper.RebuildSearchResults(
            UnstagedFileEntries, _unstagedSearchResults, flatUsesFullPath: false, _fileStatusSearchExpandState);
        FileListLayoutHelper.RebuildSearchResults(
            ConflictedFileEntries, _conflictedSearchResults, flatUsesFullPath: false, _fileStatusSearchExpandState);
        FileListLayoutHelper.Rebuild(
            StashFileEntries, StashFiles, FileStatusListLayout, flatUsesFullPath: false, _fileStatusExpandState);
    }

    private void RebuildHistorySearchEntries()
    {
        FileListLayoutHelper.RebuildSearchResults(
            HistoryFileEntries, _historySearchResults, flatUsesFullPath: false, _historySearchExpandState);
    }

    private bool TryToggleFileStatusSearchGroup(string folderKey)
    {
        if (!IsFileStatusContentSearchActive) return false;
        var expanded = FileListLayoutHelper.IsExpanded(_fileStatusSearchExpandState, folderKey);
        _fileStatusSearchExpandState[folderKey] = !expanded;
        RebuildFileStatusSearchEntries();
        SelectionSyncRequested?.Invoke();
        return true;
    }

    private bool TryToggleHistorySearchGroup(string folderKey)
    {
        if (!IsHistoryContentSearchActive) return false;
        var expanded = FileListLayoutHelper.IsExpanded(_historySearchExpandState, folderKey);
        _historySearchExpandState[folderKey] = !expanded;
        RebuildHistorySearchEntries();
        SelectionSyncRequested?.Invoke();
        return true;
    }
}

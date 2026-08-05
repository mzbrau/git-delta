using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeReviewr.App.ViewModels;

public partial class ReviewViewModel
{
    private const int ContentSearchDebounceMs = 180;

    private CancellationTokenSource? _prSearchCts;
    private readonly Dictionary<string, bool> _prSearchExpandState = new(StringComparer.Ordinal);
    private List<(FileItemViewModel File, IReadOnlyList<ChangedLineSearch.Hit> Hits)> _prSearchResults = [];
    private (DiffSide Side, int Line)? _pendingDiffScroll;

    /// <summary>Raised on the UI thread after a diff load when a search hit requested scroll.</summary>
    public event Action<DiffSide, int>? DiffScrollRequested;

    [ObservableProperty] private FileListQueryMode _fileListQueryMode = FileListQueryMode.Filter;

    public bool IsFileListFilterMode => FileListQueryMode == FileListQueryMode.Filter;
    public bool IsFileListSearchMode => FileListQueryMode == FileListQueryMode.Search;
    public bool IsPrContentSearchActive =>
        IsFileListSearchMode && !string.IsNullOrWhiteSpace(FileFilter);

    public string FileListQueryPlaceholder =>
        IsFileListSearchMode ? "Search changes…" : "Filter files…";

    public string FileListQueryClearTip =>
        IsFileListSearchMode ? "Clear search" : "Clear filter";

    public Material.Icons.MaterialIconKind FileListQueryModeIcon =>
        IsFileListSearchMode
            ? Material.Icons.MaterialIconKind.Magnify
            : Material.Icons.MaterialIconKind.FilterVariant;

    partial void OnFileListQueryModeChanged(FileListQueryMode value)
    {
        OnPropertyChanged(nameof(IsFileListFilterMode));
        OnPropertyChanged(nameof(IsFileListSearchMode));
        OnPropertyChanged(nameof(IsPrContentSearchActive));
        OnPropertyChanged(nameof(FileListQueryPlaceholder));
        OnPropertyChanged(nameof(FileListQueryClearTip));
        OnPropertyChanged(nameof(FileListQueryModeIcon));
        ApplyPrFileFilter();
    }

    [RelayCommand]
    private void SetFileListQueryMode(FileListQueryMode mode) => FileListQueryMode = mode;

    public void SelectSearchHit(FileItemViewModel file, DiffSide side, int line)
    {
        _pendingDiffScroll = (side, line);
        var alreadySelected = ReferenceEquals(SelectedFile, file)
                              || (SelectedFile is not null
                                  && string.Equals(SelectedFile.Path.Value, file.Path.Value, StringComparison.Ordinal));

        if (alreadySelected)
        {
            // Same selection skips OnSelectedFileChanged; drain pending scroll or force a reload.
            if (!ReferenceEquals(SelectedFile, file))
                SelectedFile = file;

            if (DiffRows.Count > 0)
                RequestPendingDiffScrollIfAny();
            else
                _ = LoadDiffForSelectionAsync(file);
            return;
        }

        SelectedFile = file;
    }

    private void RequestPendingDiffScrollIfAny()
    {
        if (_pendingDiffScroll is not { } pending)
            return;

        _pendingDiffScroll = null;
        DiffScrollRequested?.Invoke(pending.Side, pending.Line);
    }

    private void SchedulePrContentSearch()
    {
        _prSearchCts?.Cancel();
        _prSearchCts = new CancellationTokenSource();
        var ct = _prSearchCts.Token;
        _ = RunPrContentSearchAsync(FileFilter, ct);
    }

    private async Task RunPrContentSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ContentSearchDebounceMs, ct).ConfigureAwait(false);
            var session = _session;
            if (session is null) return;

            List<FileItemViewModel> files = [];
            await InvokeOnUiAsync(() => files = PrFiles.ToList()).ConfigureAwait(false);

            var options = BuildDiffOptions();
            var concurrency = DiffWarmStore.ClampConcurrency(_settings.Current.DiffPrefetchConcurrency);
            var gate = new SemaphoreSlim(concurrency, concurrency);
            var tasks = files.Select(async file =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var diff = await _reviewService
                        .GetDiffAsync(session, file.Path, options, ct)
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
            var results = new List<(FileItemViewModel File, IReadOnlyList<ChangedLineSearch.Hit> Hits)>();
            foreach (var (file, hits) in completed)
            {
                if (hits.Count > 0)
                    results.Add((file, hits));
            }

            await InvokeOnUiAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                if (!IsPrContentSearchActive) return;
                if (!string.Equals(FileFilter.Trim(), query.Trim(), StringComparison.Ordinal))
                    return;
                if (!ReferenceEquals(_session, session)) return;

                _prSearchResults = results;
                ApplyPrSearchResults();
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
    }

    private void ApplyPrSearchResults()
    {
        InvokeOnUiAsync(() =>
        {
            var selectedPath = SelectedFile?.Path.Value;

            FilteredPrFiles.Clear();
            foreach (var (file, _) in _prSearchResults)
                FilteredPrFiles.Add(file);

            RebuildPrFileEntries();

            if (selectedPath is not null)
            {
                var restored = FilteredPrFiles.FirstOrDefault(f =>
                                   string.Equals(f.Path.Value, selectedPath, StringComparison.Ordinal))
                               ?? FilteredPrFiles.FirstOrDefault();
                var sameRef = ReferenceEquals(SelectedFile, restored);
                SelectedFile = restored;

                if (sameRef &&
                    restored is not null &&
                    DiffRows.Count == 0 &&
                    !IsLoadingDiff)
                {
                    _ = LoadDiffForSelectionAsync(restored);
                }
            }

            UpdateProgressSummary();
        }).GetAwaiter().GetResult();
    }

    private void RebuildPrSearchEntries()
    {
        _suppressPrEntrySync = true;
        try
        {
            SelectedPrFileEntry = null;
            SelectionClearRequested?.Invoke();

            FileListLayoutHelper.RebuildSearchResults(
                PrFileEntries,
                _prSearchResults,
                flatUsesFullPath: true,
                _prSearchExpandState);

            if (SelectedFile is null)
            {
                SelectedPrFileEntry = null;
                return;
            }

            SelectedPrFileEntry = PrFileEntries.FirstOrDefault(e =>
                (e.IsFile || e.IsSearchGroup) &&
                e.File is not null &&
                string.Equals(e.File.Path.Value, SelectedFile.Path.Value, StringComparison.Ordinal));
        }
        finally
        {
            _suppressPrEntrySync = false;
        }
    }

    private bool TryTogglePrSearchGroup(string folderKey)
    {
        if (!IsPrContentSearchActive) return false;
        var expanded = FileListLayoutHelper.IsExpanded(_prSearchExpandState, folderKey);
        _prSearchExpandState[folderKey] = !expanded;
        RebuildPrSearchEntries();
        return true;
    }
}

using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using GitDelta.App.Collections;
using GitDelta.App.Controls;
using GitDelta.App.Services;
using GitDelta.Core;
using GitDelta.Core.AI;
using GitDelta.Core.Diagnostics;
using GitDelta.Core.Diff;
using GitDelta.Diff;

namespace GitDelta.App.ViewModels;

public partial class WorkingCopyViewModel
{
    /// <summary>
    /// Owns working-copy diff load / SWR / present / syntax / prefetch helpers.
    /// The outer view-model remains the AXAML façade and forwards here.
    /// </summary>
    private sealed class WorkingCopyDiffPresenter(WorkingCopyViewModel vm)
    {
        private readonly WorkingCopyViewModel _vm = vm;

    public void ExpandCollapsedSection(int hunkIndex, int lineIndexInHunk)
    {
        if (!_vm._expandedCollapses.Add((hunkIndex, lineIndexInHunk))) return;
        if (_vm._currentDiff is not null)
            ProjectRows(_vm._currentDiff);
    }

    public async Task LoadDiffForSelectionAsync(FileItemViewModel? file)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _vm._diffCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        _vm._markdownCts?.Cancel();
        _vm._markdownCts = null;
        var ct = cts.Token;

        _vm._expandedCollapses.Clear();
        _vm.SelectedAddedLines = 0;
        _vm.SelectedRemovedLines = 0;
        _vm.ClearImagePreview();
        if (file is null || _vm._repoPath is null)
        {
            _vm.DiffRows.Clear();
            _vm._currentDiff = null;
            ClearDiffCacheState();
            _vm.DiffEmptyMessage = _vm.IsHistoryMode
                ? _vm.SelectedCommit is null ? "Select a commit" : "Select a file to view its diff"
                : "Select a file to view its diff";
            _vm.OnPropertyChanged(nameof(_vm.DiffFooterText));
            if (!_vm.IsHistoryMode && !_vm.IsStashMode)
                _vm.PendingReview.OnFileSelectionChanged(null, null);
            return;
        }

        if (_vm.IsStashMode)
        {
            await _vm.LoadStashDiffAsync(file, cts, ct);
            return;
        }

        if (_vm.IsHistoryMode)
        {
            await _vm.LoadCommitDiffAsync(file, cts, ct);
            return;
        }

        var target = _vm.IsCombinedReviewMode
            ? DiffTarget.HeadToWorktree
            : file.IsStagedList ? DiffTarget.HeadToIndex : DiffTarget.IndexToWorktree;

        _vm.CanStageFromDiff = target is DiffTarget.IndexToWorktree or DiffTarget.HeadToIndex;
        _vm.StagingDisabledReason = target == DiffTarget.HeadToWorktree
            ? "Combined review mode is read-only. Partial staging requires the staged/unstaged lists."
            : file.IsConflicted ? "Conflicted files cannot be staged here. Resolve externally or open mergetool."
            : null;
        _vm.OnPropertyChanged(nameof(_vm.CanStageLines));
        _vm.OnPropertyChanged(nameof(_vm.CanUnstageLines));
        _vm.OnPropertyChanged(nameof(_vm.CanDiscardLines));

        var options = _vm.BuildDiffOptions();
        var key = FileStatusWarmKey(file.Path, target, options);

        try
        {
            using var loadActivity = GitDeltaActivity.Source.StartActivity("diff.load");
            loadActivity?.SetTag("diff.path", file.Path.Value);
            loadActivity?.SetTag("diff.target", target.ToString());
            loadActivity?.SetTag("diff.view_mode", _vm.ViewMode.ToString());

            var sw = Stopwatch.StartNew();
            if (file.Kind == ChangeKind.Untracked)
            {
                await LoadTrackedDiffWithSwrAsync(
                    file,
                    key,
                    target,
                    force: false,
                    factory: token => LoadUntrackedFileDiffAsync(_vm._repoPath, file.Path, target, token),
                    cts,
                    ct);
            }
            else
            {
                await LoadTrackedDiffWithSwrAsync(
                    file,
                    key,
                    target,
                    force: false,
                    factory: token => _vm._diffService.GetDiffAsync(_vm._repoPath, file.Path, target.AsWorkingCopy(), options, token),
                    cts,
                    ct);
            }

            loadActivity?.SetTag("diff.row_count", _vm.DiffRows.Count);
            GitDeltaMeters.DiffGenerationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) { }
        catch (DiffTooLargeException ex)
        {
            if (!ReferenceEquals(_vm._diffCts, cts) || !ReferenceEquals(_vm.SelectedFile, file))
                return;
            _vm.SelectedAddedLines = 0;
            _vm.SelectedRemovedLines = 0;
            _vm.ClearImagePreview();
            _vm.DiffRows.Clear();
            _vm._currentDiff = null;
            ClearDiffCacheState();
            _vm.DiffEmptyMessage = ex.Message;
            _vm._notifications.Error(ex.Message, exception: ex);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_vm._diffCts, cts) || !ReferenceEquals(_vm.SelectedFile, file))
                return;
            _vm.SelectedAddedLines = 0;
            _vm.SelectedRemovedLines = 0;
            _vm.ClearImagePreview();
            _vm.DiffRows.Clear();
            _vm._currentDiff = null;
            ClearDiffCacheState();
            _vm._notifications.Error($"Diff failed: {ex.Message}", () => _ = LoadDiffForSelectionAsync(file), ex);
        }
        finally
        {
            // Only the current load may clear the spinner; a superseded load's finally must
            // not hide loading for the newer request.
            if (Interlocked.CompareExchange(ref _vm._diffCts, null, cts) == cts)
            {
                _vm.IsLoadingDiff = false;
                _vm.IsDiffRefreshing = false;
                UpdateDiffCacheState(key);
                UpdateFileCacheIndicators();
                _vm.OnPropertyChanged(nameof(_vm.DiffFooterText));
                if (!ct.IsCancellationRequested && ReferenceEquals(_vm.SelectedFile, file))
                    _vm.PendingReview.OnFileSelectionChanged(file, _vm._currentDiff);
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Stale-while-revalidate load: paint a warm (possibly stale) hit immediately, then refresh in
    /// the background when needed. Only clears the viewer when there is no usable cache — including
    /// keeping a same-path painted (or alternate-target warm) diff across stage/unstage target flips.
    /// </summary>
    public async Task LoadTrackedDiffWithSwrAsync(
        FileItemViewModel file,
        DiffWarmKey key,
        DiffTarget target,
        bool force,
        Func<CancellationToken, Task<FileDiff>> factory,
        CancellationTokenSource cts,
        CancellationToken ct)
    {
        DiffWarmEntry? entry = null;
        var hasWarmHit = _vm._warmStore.TryGetCompleted(key, out entry) && entry is not null;
        var needsRefresh = force || !hasWarmHit || entry!.IsStale;

        if (hasWarmHit)
        {
            _vm.IsLoadingDiff = false;
            ApplyDiffCacheState(entry!);
            await PresentDiffAsync(file, entry!.Diff, target, cts, ct);
            if (!needsRefresh)
                return;

            _vm.IsDiffRefreshing = true;
        }
        else if (TryGetAlternateTargetWarmEntry(key, out var altEntry) && altEntry is not null)
        {
            // Stage/unstage flips DiffTarget; reuse the previous target's cached diff as a stand-in.
            _vm.IsLoadingDiff = false;
            _vm.IsDiffRefreshing = true;
            ApplyDiffCacheState(altEntry with { IsStale = true });
            await PresentDiffAsync(file, altEntry.Diff with { Scope = target.AsWorkingCopy() }, target, cts, ct);
        }
        else if (HasPaintedDiffForPath(file.Path.Value))
        {
            // Keep whatever is already on screen for this path until the new target arrives.
            _vm.IsLoadingDiff = false;
            _vm.IsDiffRefreshing = true;
            if (_vm._currentDiff is not null && _vm._currentDiff.Scope.WorkingCopyTargetOrNull() != target)
            {
                _vm._currentDiff = _vm._currentDiff with { Scope = target.AsWorkingCopy() };
                _vm.OnPropertyChanged(nameof(_vm.CanStageLines));
                _vm.OnPropertyChanged(nameof(_vm.CanUnstageLines));
                _vm.OnPropertyChanged(nameof(_vm.CanDiscardLines));
            }

            if (_vm._diffCacheCompletedAt is { } at)
                _vm.DiffCacheAgeText = FormatCacheAge(at);
            _vm.HasDiffCache = _vm.DiffRows.Count > 0;
        }
        else
        {
            _vm.DiffRows.Clear();
            _vm._currentDiff = null;
            ClearDiffCacheState();
            _vm.IsLoadingDiff = true;
            _vm.IsDiffRefreshing = false;
        }

        var loadTask = _vm._warmStore.GetOrStart(key, factory, force);
        var diff = await loadTask.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_vm._diffCts, cts) || !ReferenceEquals(_vm.SelectedFile, file))
            return;
        await PresentDiffAsync(file, diff, target, cts, ct);
        if (ReferenceEquals(_vm._diffCts, cts))
            UpdateDiffCacheState(key);
    }

    public bool HasPaintedDiffForPath(string path)
    {
        if (_vm.DiffRows.Count == 0 || _vm._currentDiff is null)
            return false;

        return string.Equals(_vm._currentDiff.NewPath.Value, path, StringComparison.Ordinal)
               || string.Equals(_vm._currentDiff.OldPath.Value, path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Looks up a completed warm entry for the same path/scope/options under an alternate
    /// <see cref="DiffTarget"/> (used when stage/unstage flips IndexToWorktree ↔ HeadToIndex).
    /// </summary>
    public bool TryGetAlternateTargetWarmEntry(DiffWarmKey key, out DiffWarmEntry? entry)
    {
        foreach (var alt in AlternateDiffScopes(key.DiffScope))
        {
            var altKey = new DiffWarmKey(key.Scope, key.Path, alt, key.Options);
            if (_vm._warmStore.TryGetCompleted(altKey, out entry) && entry is not null)
                return true;
        }

        entry = null;
        return false;
    }

    public static IEnumerable<DiffScope> AlternateDiffScopes(DiffScope scope)
    {
        if (scope is not DiffScope.WorkingCopy wc)
            yield break;

        foreach (var alt in AlternateDiffTargets(wc.Target))
            yield return alt.AsWorkingCopy();
    }

    public static IEnumerable<DiffTarget> AlternateDiffTargets(DiffTarget target) =>
        target switch
        {
            DiffTarget.IndexToWorktree => [DiffTarget.HeadToIndex, DiffTarget.HeadToWorktree],
            DiffTarget.HeadToIndex => [DiffTarget.IndexToWorktree, DiffTarget.HeadToWorktree],
            DiffTarget.HeadToWorktree => [DiffTarget.IndexToWorktree, DiffTarget.HeadToIndex],
            _ => [],
        };

    public async Task RevalidateSelectedDiffAfterStatusAsync(
        RepositoryStatus? previousStatus,
        RepositoryStatus currentStatus)
    {
        if (!_vm.IsFileStatusMode || _vm.SelectedFile is null)
            return;

        // Soft status refresh must not exit history browse or replace the painted history diff.
        if (_vm.PendingReview.FileHistoryBrowse.IsFileHistoryBrowseMode)
            return;

        if (!IsPathInWorkingLists(_vm.SelectedFile.Path.Value, _vm.SelectedFile.IsStagedList))
        {
            if (_vm.IsRecentOnlySelection(_vm.SelectedFile))
            {
                // Re-check after awaits above — browse may have started since the early return.
                if (_vm.PendingReview.FileHistoryBrowse.IsFileHistoryBrowseMode)
                    return;
                await _vm.ReloadCleanFileDiffAsync(_vm.SelectedFile, CancellationToken.None);
                return;
            }

            _vm._warmStore.InvalidatePath(_vm.SelectedFile.Path.Value);
            _vm.DiffRows.Clear();
            _vm._currentDiff = null;
            ClearDiffCacheState();
            _vm.ClearImagePreview();
            _vm.DiffEmptyMessage = "Select a file to view its diff";
            _vm.SelectedAddedLines = 0;
            _vm.SelectedRemovedLines = 0;
            _vm.IsLoadingDiff = false;
            _vm.IsDiffRefreshing = false;
            _vm.OnPropertyChanged(nameof(_vm.DiffFooterText));
            _vm.PendingReview.OnFileSelectionChanged(null, null);
            return;
        }

        if (IsSelectedFileContentUnchanged(previousStatus, currentStatus, _vm.SelectedFile))
        {
            // Remapped FileItemViewModel — refresh band/guidance without wiping AI annotations.
            _vm.PendingReview.OnFileSelectionChanged(_vm.SelectedFile, _vm._currentDiff);
            return;
        }

        await LoadDiffForSelectionAsync(_vm.SelectedFile);
    }

    public void SoftInvalidateChangedPaths(RepositoryStatus? previous, RepositoryStatus current)
    {
        if (previous is null)
        {
            _vm._warmStore.SoftInvalidateScope("fs");
            return;
        }

        var prev = BuildPathOidFingerprint(previous);
        var curr = BuildPathOidFingerprint(current);
        foreach (var (path, fingerprint) in curr)
        {
            if (!prev.TryGetValue(path, out var old) || !string.Equals(old, fingerprint, StringComparison.Ordinal))
                _vm._warmStore.SoftInvalidatePath(path);
        }

        foreach (var path in prev.Keys)
        {
            if (!curr.ContainsKey(path))
                _vm._warmStore.SoftInvalidatePath(path);
        }
    }

    public static Dictionary<string, string> BuildPathOidFingerprint(RepositoryStatus status)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(StatusEntry e)
        {
            var fp = $"{e.Kind}|{e.HeadOid?.Value}|{e.IndexOid?.Value}|{e.WorktreeOid?.Value}|{e.IsStaged}|{e.IsUnstaged}";
            if (map.TryGetValue(e.Path.Value, out var existing))
                map[e.Path.Value] = existing + ";" + fp;
            else
                map[e.Path.Value] = fp;
        }

        foreach (var e in status.Staged) Add(e);
        foreach (var e in status.Unstaged) Add(e);
        foreach (var e in status.Conflicted) Add(e);
        return map;
    }

    public static bool IsSelectedFileContentUnchanged(
        RepositoryStatus? previous,
        RepositoryStatus current,
        FileItemViewModel selected)
    {
        if (previous is null)
            return false;

        var path = selected.Path.Value;
        var prev = BuildPathOidFingerprint(previous);
        var curr = BuildPathOidFingerprint(current);
        return prev.TryGetValue(path, out var oldFp)
               && curr.TryGetValue(path, out var newFp)
               && string.Equals(oldFp, newFp, StringComparison.Ordinal);
    }

    public bool IsPathInWorkingLists(string path, bool preferStaged)
    {
        bool Match(FileItemViewModel f) =>
            string.Equals(f.Path.Value, path, StringComparison.Ordinal);

        if (preferStaged && _vm._allStaged.Any(Match)) return true;
        if (!preferStaged && _vm._allUnstaged.Any(Match)) return true;
        if (_vm._allStaged.Any(Match) || _vm._allUnstaged.Any(Match) || _vm._allConflicted.Any(Match))
            return true;
        return false;
    }

    public void ClearDiffCacheState()
    {
        _vm._diffCacheCompletedAt = null;
        _vm.HasDiffCache = false;
        _vm.DiffCacheAgeText = null;
    }

    public void ApplyDiffCacheState(DiffWarmEntry entry)
    {
        _vm._diffCacheCompletedAt = entry.CompletedAt;
        _vm.HasDiffCache = true;
        _vm.DiffCacheAgeText = FormatCacheAge(entry.CompletedAt);
    }

    public void UpdateDiffCacheState(DiffWarmKey key)
    {
        if (_vm._warmStore.TryGetCompleted(key, out DiffWarmEntry? entry) && entry is not null)
            ApplyDiffCacheState(entry);
        else
            ClearDiffCacheState();
    }

    public static string FormatCacheAge(DateTimeOffset completedAt)
    {
        var ago = DateTimeOffset.UtcNow - completedAt;
        if (ago.TotalSeconds < 5) return "Cached just now";
        if (ago.TotalMinutes < 1) return $"Cached {(int)ago.TotalSeconds}s ago";
        if (ago.TotalHours < 1) return $"Cached {(int)ago.TotalMinutes}m ago";
        if (ago.TotalDays < 1) return $"Cached {(int)ago.TotalHours}h ago";
        return $"Cached {(int)ago.TotalDays}d ago";
    }

    public void UpdateFileCacheIndicators()
    {
        using var activity = GitDeltaActivity.Source.StartActivity("wc.cache.indicators");
        var sw = Stopwatch.StartNew();
        try
        {
            var options = _vm.BuildDiffOptions();
            var scanned = 0;
            var statsApplied = 0;

            void UpdateFs(FileItemViewModel file)
            {
                scanned++;
                var target = _vm.IsCombinedReviewMode
                    ? DiffTarget.HeadToWorktree
                    : file.IsStagedList ? DiffTarget.HeadToIndex : DiffTarget.IndexToWorktree;
                var key = FileStatusWarmKey(file.Path, target, options);
                if (_vm._warmStore.TryGetCompleted(key, out DiffWarmEntry? entry) && entry is not null)
                {
                    if (!file.HasCachedDiff)
                        file.HasCachedDiff = true;
                    if (file.IsDiffStale != entry.IsStale)
                        file.IsDiffStale = entry.IsStale;
                    var stats = FileChangeStats.FromDiff(entry.Diff);
                    if (file.LinesAdded != stats.LinesAdded
                        || file.LinesRemoved != stats.LinesRemoved
                        || file.ChangePercent != stats.ChangePercent)
                    {
                        file.ApplyChangeStats(stats);
                        statsApplied++;
                    }
                }
                else
                {
                    if (file.HasCachedDiff)
                        file.HasCachedDiff = false;
                    if (file.IsDiffStale)
                        file.IsDiffStale = false;
                }
            }

            foreach (var f in _vm._allStaged) UpdateFs(f);
            foreach (var f in _vm._allUnstaged) UpdateFs(f);
            foreach (var f in _vm._allConflicted) UpdateFs(f);

            if (_vm.SelectedStash is { } stash)
            {
                foreach (var file in _vm.StashFiles)
                {
                    scanned++;
                    var key = StashWarmKey(stash.Index, file.Path, options);
                    if (_vm._warmStore.TryGetCompleted(key, out DiffWarmEntry? entry) && entry is not null)
                    {
                        if (!file.HasCachedDiff)
                            file.HasCachedDiff = true;
                        if (file.IsDiffStale != entry.IsStale)
                            file.IsDiffStale = entry.IsStale;
                        var stats = FileChangeStats.FromDiff(entry.Diff);
                        if (file.LinesAdded != stats.LinesAdded
                            || file.LinesRemoved != stats.LinesRemoved
                            || file.ChangePercent != stats.ChangePercent)
                        {
                            file.ApplyChangeStats(stats);
                            statsApplied++;
                        }
                    }
                    else
                    {
                        if (file.HasCachedDiff)
                            file.HasCachedDiff = false;
                        if (file.IsDiffStale)
                            file.IsDiffStale = false;
                    }
                }
            }

            if (_vm.SelectedCommit is { } commit)
            {
                foreach (var file in _vm._allHistoryFiles)
                {
                    scanned++;
                    var key = HistoryWarmKey(commit.Oid, file.Path, options);
                    if (_vm._warmStore.TryGetCompleted(key, out DiffWarmEntry? entry) && entry is not null)
                    {
                        if (!file.HasCachedDiff)
                            file.HasCachedDiff = true;
                        if (file.IsDiffStale != entry.IsStale)
                            file.IsDiffStale = entry.IsStale;
                        var stats = FileChangeStats.FromDiff(entry.Diff);
                        if (file.LinesAdded != stats.LinesAdded
                            || file.LinesRemoved != stats.LinesRemoved
                            || file.ChangePercent != stats.ChangePercent)
                        {
                            file.ApplyChangeStats(stats);
                            statsApplied++;
                        }
                    }
                    else
                    {
                        if (file.HasCachedDiff)
                            file.HasCachedDiff = false;
                        if (file.IsDiffStale)
                            file.IsDiffStale = false;
                    }
                }
            }

            activity?.SetTag("wc.staged_count", _vm._allStaged.Count);
            activity?.SetTag("wc.unstaged_count", _vm._allUnstaged.Count);
            activity?.SetTag("wc.conflicted_count", _vm._allConflicted.Count);
            activity?.SetTag("indicators.scanned_count", scanned);
            activity?.SetTag("indicators.stats_applied_count", statsApplied);
        }
        finally
        {
            GitDeltaMeters.WcCacheIndicatorsMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task PresentDiffAsync(
        FileItemViewModel file,
        FileDiff diff,
        DiffTarget target,
        CancellationTokenSource cts,
        CancellationToken ct)
    {
        using var presentActivity = GitDeltaActivity.Source.StartActivity("diff.present");
        presentActivity?.SetTag("diff.path", file.Path.Value);
        presentActivity?.SetTag("diff.view_mode", _vm.ViewMode.ToString());
        var presentSw = Stopwatch.StartNew();

        var viewMode = _vm.ViewMode;
        var showFullFile = _vm.ShowFullFile;
        var expanded = SnapshotExpandedCollapses();

        // Enrich + project off the UI thread — ApplyIntraLine/projectors are CPU-bound.
        IReadOnlyList<DiffRow> rows;
        FileDiff enriched;
        using (var projectActivity = GitDeltaActivity.Source.StartActivity("diff.project"))
        {
            var projectSw = Stopwatch.StartNew();
            (enriched, rows) = await Task.Run(() =>
            {
                var withIntra = EnsureIntraLine(diff);
                var projected = BuildProjectedRows(withIntra, viewMode, showFullFile, expanded);
                return (withIntra, projected);
            }, ct).ConfigureAwait(true);
            GitDeltaMeters.DiffProjectMs.Record(projectSw.Elapsed.TotalMilliseconds);
            projectActivity?.SetTag("diff.row_count", rows.Count);
        }

        ct.ThrowIfCancellationRequested();
        // A newer selection may have started while we awaited projection.
        if (!ReferenceEquals(_vm._diffCts, cts) || !ReferenceEquals(_vm.SelectedFile, file))
            return;

        var sameContent = _vm._currentDiff is not null
                          && string.Equals(_vm._currentDiff.OldContent.Value, enriched.OldContent.Value, StringComparison.Ordinal)
                          && string.Equals(_vm._currentDiff.NewContent.Value, enriched.NewContent.Value, StringComparison.Ordinal)
                          && _vm._currentDiffTarget == target
                          && _vm.DiffRows.Count == rows.Count
                          && !IsImagePath(file.Path.Value)
                          && !enriched.IsBinary;

        _vm._currentDiff = enriched;
        _vm._currentDiffTarget = target;
        _vm.UpdateDiffStats(_vm._currentDiff);
        _vm.NotifyMarkdownPreviewStateChanged();

        if (IsImagePath(file.Path.Value))
        {
            ClearSyntaxTokens();
            _vm.ClearMarkdownPreviewText();
            await _vm.LoadImagePreviewAsync(file, _vm._currentDiff, target, ct);
            if (!ReferenceEquals(_vm._diffCts, cts) || !ReferenceEquals(_vm.SelectedFile, file))
                return;
            _vm.DiffRows.Reset([]);
            _vm.DiffEmptyMessage = "";
        }
        else if (_vm._currentDiff.IsBinary)
        {
            ClearSyntaxTokens();
            _vm.ClearMarkdownPreviewText();
            _vm.DiffRows.Reset([]);
            _vm.DiffEmptyMessage = "Binary file";
            _vm.IsImagePreview = false;
        }
        else if (sameContent)
        {
            // Keep painted rows — avoids InvalidateMeasure/paint-cache thrash on soft focus refresh.
            _vm.DiffEmptyMessage = "Select a file to view its diff";
            presentActivity?.SetTag("diff.row_count", rows.Count);
            presentActivity?.SetTag("diff.present_skipped", true);
        }
        else
        {
            _vm.DiffEmptyMessage = "Select a file to view its diff";
            _vm.DiffRows.Reset(rows);
            presentActivity?.SetTag("diff.row_count", rows.Count);
            // Tokenise once per selected FileDiff; view-mode switches reuse these tokens.
            await LoadSyntaxTokensAsync(file, _vm._currentDiff, target, ct);
            if (!ReferenceEquals(_vm._diffCts, cts) || !ReferenceEquals(_vm.SelectedFile, file))
                return;
            if (_vm.ShowMarkdownPreviewPane)
                await LoadMarkdownPreviewTextAsync(file, _vm._currentDiff, target, ct);
            else
                _vm.ClearMarkdownPreviewText();
        }

        if (!ReferenceEquals(_vm._diffCts, cts) || !ReferenceEquals(_vm.SelectedFile, file))
            return;

        GitDeltaMeters.DiffPresentMs.Record(presentSw.Elapsed.TotalMilliseconds);
        _vm.OnPropertyChanged(nameof(_vm.CanStageLines));
        _vm.OnPropertyChanged(nameof(_vm.CanUnstageLines));
        _vm.OnPropertyChanged(nameof(_vm.CanDiscardLines));
        _vm.RequestPendingDiffScrollIfAny();
    }

    public async Task LoadMarkdownPreviewTextAsync(
        FileItemViewModel file,
        FileDiff diff,
        DiffTarget target,
        CancellationToken ct)
    {
        if (!MarkdownPath.IsMarkdownPath(file.Path.Value))
        {
            _vm.ClearMarkdownPreviewText();
            return;
        }

        try
        {
            var text = await ReadSideTextAsync(diff.NewContent, file, target, sideIsNew: true, ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await InvokeOnUiAsync(() => _vm.MarkdownPreviewText = text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await InvokeOnUiAsync(_vm.ClearMarkdownPreviewText);
        }
    }

    public void ClearSyntaxTokens()
    {
        _vm.LeftSyntaxTokens = null;
        _vm.RightSyntaxTokens = null;
    }

    public async Task LoadSyntaxTokensAsync(
        FileItemViewModel file,
        FileDiff diff,
        DiffTarget target,
        CancellationToken ct)
    {
        using var activity = GitDeltaActivity.Source.StartActivity("diff.syntax");
        activity?.SetTag("diff.path", file.Path.Value);

        if (_vm._syntaxTokens is null || _vm._repoPath is null)
        {
            ClearSyntaxTokens();
            return;
        }

        try
        {
            var leftText = await ReadSideTextAsync(diff.OldContent, file, target, sideIsNew: false, ct)
                .ConfigureAwait(false);
            var rightText = await ReadSideTextAsync(diff.NewContent, file, target, sideIsNew: true, ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            FileSyntaxTokens? left = null;
            FileSyntaxTokens? right = null;
            if (leftText is not null)
            {
                left = await _vm._syntaxTokens.TokeniseAsync(diff.OldContent, file.Path, leftText, ct)
                    .ConfigureAwait(false);
            }

            if (rightText is not null)
            {
                right = await _vm._syntaxTokens.TokeniseAsync(diff.NewContent, file.Path, rightText, ct)
                    .ConfigureAwait(false);
            }

            await InvokeOnUiAsync(() =>
            {
                _vm.LeftSyntaxTokens = left;
                _vm.RightSyntaxTokens = right;
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await InvokeOnUiAsync(ClearSyntaxTokens);
        }
    }

    public async Task<string?> ReadSideTextAsync(
        ContentId content,
        FileItemViewModel file,
        DiffTarget target,
        bool sideIsNew,
        CancellationToken ct)
    {
        if (_vm._repoPath is null) return null;

        if (sideIsNew
            && (target is DiffTarget.IndexToWorktree or DiffTarget.HeadToWorktree
                || file.Kind == ChangeKind.Untracked))
        {
            var worktreePath = RepositoryPathResolver.ResolveUnderRoot(_vm._repoPath, file.Path);
            if (System.IO.File.Exists(worktreePath))
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(worktreePath, ct).ConfigureAwait(false);
                return DecodeUtf8(bytes);
            }
        }

        if (content.IsEmpty) return null;
        var blob = await _vm._objects.ReadBlobAsync(_vm._repoPath, content, ct).ConfigureAwait(false);
        return DecodeUtf8(blob);
    }

    public static string? DecodeUtf8(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        // Skip UTF-8 BOM if present.
        var offset = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    public async Task<FileDiff> LoadUntrackedFileDiffAsync(
        string repoPath,
        FilePath path,
        DiffTarget target,
        CancellationToken ct)
    {
        var fullPath = RepositoryPathResolver.ResolveUnderRoot(repoPath, path);
        if (!System.IO.File.Exists(fullPath))
            return UntrackedFileDiff.Create(path, string.Empty, target);

        var maxBytes = _vm._settings.Current.MaxDiffPatchBytes;
        var info = new System.IO.FileInfo(fullPath);
        if (info.Length > maxBytes)
            throw new DiffTooLargeException(maxBytes, info.Length);

        var bytes = await System.IO.File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (bytes.LongLength > maxBytes)
            throw new DiffTooLargeException(maxBytes, bytes.LongLength);
        return UntrackedFileDiff.Create(path, bytes, target);
    }

    public static DiffWarmKey FileStatusWarmKey(FilePath path, DiffTarget target, DiffOptions options) =>
        new("fs", path.Value, target.AsWorkingCopy(), options);

    public static DiffWarmKey HistoryWarmKey(string oid, FilePath path, DiffOptions options) =>
        new($"hist:{oid}", path.Value, DiffTarget.HeadToWorktree.AsWorkingCopy(), options);

    public static DiffWarmKey StashWarmKey(int index, FilePath path, DiffOptions options) =>
        new($"stash:{index}", path.Value, DiffTarget.HeadToWorktree.AsWorkingCopy(), options);

    public void ScheduleFileStatusPrefetch()
    {
        if (_vm._repoPath is null || !_vm.IsFileStatusMode) return;
        _vm._prefetchCts?.Cancel();
        _vm._prefetchCts = new CancellationTokenSource();
        var ct = _vm._prefetchCts.Token;
        _ = PrefetchFileStatusDiffsAsync(ct);
    }

    public async Task PrefetchFileStatusDiffsAsync(CancellationToken ct)
    {
        if (_vm._repoPath is null) return;

        var options = _vm.BuildDiffOptions();
        var settings = _vm._settings.Current;
        var priorityPaths = ClampPrefetchPriorityPaths(settings.DiffPrefetchPriorityPaths);
        var ordered = BuildFileStatusPrefetchOrder(
            ClampPrefetchNeighborRadius(settings.DiffPrefetchNeighborRadius));
        var priorityCount = Math.Min(priorityPaths, ordered.Count);
        var priority = ordered.GetRange(0, priorityCount);
        var drip = ordered.Count > priorityCount
            ? ordered.GetRange(priorityCount, ordered.Count - priorityCount)
            : [];

        using (var activity = GitDeltaActivity.Source.StartActivity("wc.prefetch"))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                activity?.SetTag("prefetch.path_count", priority.Count);
                activity?.SetTag("prefetch.priority_cap", priorityPaths);
                activity?.SetTag("prefetch.drip_total", drip.Count);
                activity?.SetTag("prefetch.concurrency", _vm._warmStore.MaxConcurrencyLimit);
                activity?.SetTag(
                    "prefetch.drip_delay_ms",
                    ClampPrefetchDripDelayMs(settings.DiffPrefetchDripDelayMs));
                var started = 0;
                var enqueueSw = Stopwatch.StartNew();
                foreach (var (path, target, kind) in priority)
                {
                    if (ct.IsCancellationRequested) break;

                    var key = FileStatusWarmKey(path, target, options);
                    if (_vm._warmStore.TryGetCompleted(key, out DiffWarmEntry? entry)
                        && entry is { IsStale: false })
                        continue;

                    _ = StartFileStatusWarm(path, target, kind, options);
                    started++;
                }

                GitDeltaMeters.WcPrefetchEnqueueMs.Record(enqueueSw.Elapsed.TotalMilliseconds);
                activity?.SetTag("prefetch.priority_count", started);
                activity?.SetTag("prefetch.started_count", started);

                await Task.Yield();
                await InvokeOnUiAsync(UpdateFileCacheIndicators, "cache_indicators").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                activity?.SetTag("prefetch.cancelled", true);
            }
            finally
            {
                GitDeltaMeters.WcPrefetchMs.Record(sw.Elapsed.TotalMilliseconds);
            }
        }

        if (drip.Count == 0 || ct.IsCancellationRequested)
            return;

        using var dripActivity = GitDeltaActivity.Source.StartActivity("wc.prefetch.drip");
        dripActivity?.SetTag("prefetch.drip_total", drip.Count);
        var completed = 0;
        var indicatorSw = Stopwatch.StartNew();
        try
        {
            foreach (var (path, target, kind) in drip)
            {
                ct.ThrowIfCancellationRequested();

                var key = FileStatusWarmKey(path, target, options);
                if (_vm._warmStore.TryGetCompleted(key, out DiffWarmEntry? entry)
                    && entry is { IsStale: false })
                    continue;

                try
                {
                    await StartFileStatusWarm(path, target, kind, options).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Individual warm failures should not stop the drip.
                }

                completed++;
                var live = _vm._settings.Current;
                var indicatorThrottleMs = ClampPrefetchIndicatorThrottleMs(live.DiffPrefetchIndicatorThrottleMs);
                var dripDelayMs = ClampPrefetchDripDelayMs(live.DiffPrefetchDripDelayMs);
                if (indicatorSw.ElapsedMilliseconds >= indicatorThrottleMs)
                {
                    await InvokeOnUiAsync(UpdateFileCacheIndicators, "cache_indicators").ConfigureAwait(false);
                    indicatorSw.Restart();
                }

                if (dripDelayMs > 0)
                    await Task.Delay(dripDelayMs, ct).ConfigureAwait(false);
            }

            await InvokeOnUiAsync(UpdateFileCacheIndicators, "cache_indicators").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            dripActivity?.SetTag("prefetch.cancelled", true);
        }
        finally
        {
            dripActivity?.SetTag("prefetch.drip_completed", completed);
            dripActivity?.SetTag(
                "prefetch.drip_delay_ms",
                ClampPrefetchDripDelayMs(_vm._settings.Current.DiffPrefetchDripDelayMs));
        }
    }

    public Task<FileDiff> StartFileStatusWarm(
        FilePath path,
        DiffTarget target,
        ChangeKind kind,
        DiffOptions options)
    {
        var key = FileStatusWarmKey(path, target, options);
        var repoPath = _vm._repoPath!;
        if (kind == ChangeKind.Untracked)
        {
            return _vm._warmStore.GetOrStart(
                key,
                token => LoadUntrackedFileDiffAsync(repoPath, path, target, token));
        }

        return _vm._warmStore.GetOrStart(
            key,
            token => _vm._diffService.GetDiffAsync(repoPath, path, target.AsWorkingCopy(), options, token));
    }

    /// <summary>
    /// Full warm order: selection neighborhood first, then remaining visible files.
    /// Caller takes the first priority-cap paths as priority; the rest drip.
    /// </summary>
    public List<(FilePath Path, DiffTarget Target, ChangeKind Kind)> BuildFileStatusPrefetchOrder(
        int neighborRadius)
    {
        var result = new List<(FilePath, DiffTarget, ChangeKind)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(FileItemViewModel file)
        {
            var target = _vm.IsCombinedReviewMode
                ? DiffTarget.HeadToWorktree
                : file.IsStagedList ? DiffTarget.HeadToIndex : DiffTarget.IndexToWorktree;
            var key = $"{file.Path.Value}|{(int)target}|{file.IsStagedList}";
            if (!seen.Add(key)) return;
            result.Add((file.Path, target, file.Kind));
        }

        var visible = _vm.UnstagedFiles.Concat(_vm.StagedFiles).Concat(_vm.ConflictedFiles).ToList();

        if (_vm.SelectedFile is { } selected)
        {
            Add(selected);
            var idx = visible.FindIndex(f =>
                string.Equals(f.Path.Value, selected.Path.Value, StringComparison.Ordinal)
                && f.IsStagedList == selected.IsStagedList);
            if (idx >= 0)
            {
                for (var offset = 1; offset <= neighborRadius; offset++)
                {
                    if (idx - offset >= 0)
                        Add(visible[idx - offset]);
                    if (idx + offset < visible.Count)
                        Add(visible[idx + offset]);
                }
            }
        }

        foreach (var f in visible)
            Add(f);

        return result;
    }

    public static int ClampPrefetchDripDelayMs(int value) =>
        Math.Clamp(value, PrefetchDripDelayMsMin, PrefetchDripDelayMsMax);

    public static int ClampPrefetchIndicatorThrottleMs(int value) =>
        Math.Clamp(value, PrefetchIndicatorThrottleMsMin, PrefetchIndicatorThrottleMsMax);

    public static int ClampPrefetchPriorityPaths(int value) =>
        Math.Clamp(value, PrefetchPriorityPathsMin, PrefetchPriorityPathsMax);

    public static int ClampPrefetchNeighborRadius(int value) =>
        Math.Clamp(value, PrefetchNeighborRadiusMin, PrefetchNeighborRadiusMax);

    public FileDiff EnsureIntraLine(FileDiff diff) =>
        DiffPresentation.EnsureIntraLine(diff, _vm._intraLine);

    public HashSet<(int HunkIndex, int LineIndexInHunk)> SnapshotExpandedCollapses() =>
        _vm._expandedCollapses.Count == 0
            ? []
            : new HashSet<(int HunkIndex, int LineIndexInHunk)>(_vm._expandedCollapses);

    public IReadOnlyList<DiffRow> BuildProjectedRows(
        FileDiff diff,
        DiffViewMode viewMode,
        bool showFullFile,
        ISet<(int HunkIndex, int LineIndexInHunk)> expanded) =>
        DiffPresentation.ProjectRows(diff, viewMode, showFullFile, _vm._intraLine, expanded);

    public void ProjectRows(FileDiff diff) => _ = ProjectRowsAsync(diff);

    public async Task ProjectRowsAsync(FileDiff diff)
    {
        var viewMode = _vm.ViewMode;
        var showFullFile = _vm.ShowFullFile;
        var expanded = SnapshotExpandedCollapses();
        var rows = await Task.Run(() =>
            BuildProjectedRows(diff, viewMode, showFullFile, expanded)).ConfigureAwait(true);
        // Drop result if the painted diff changed while we projected.
        if (!ReferenceEquals(_vm._currentDiff, diff))
            return;
        _vm.DiffRows.Reset(rows);
    }
    }
}

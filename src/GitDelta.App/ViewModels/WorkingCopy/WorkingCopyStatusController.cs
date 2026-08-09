using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using GitDelta.App.Collections;
using GitDelta.Core;
using GitDelta.Core.AI;
using GitDelta.Core.Diagnostics;
using GitDelta.Core.Diff;

namespace GitDelta.App.ViewModels;

public partial class WorkingCopyViewModel
{
    /// <summary>
    /// Owns status-driven file-list rebuild and optimistic overlay application.
    /// </summary>
    private sealed class WorkingCopyStatusController(WorkingCopyViewModel vm)
    {
        private readonly WorkingCopyViewModel _vm = vm;

    public void RebuildFileListsTimed(RepositoryStatus status, string reason)
    {
        using var activity = GitDeltaActivity.Source.StartActivity("wc.filelists.rebuild");
        activity?.SetTag("wc.rebuild_reason", reason);
        var sw = Stopwatch.StartNew();
        try
        {
            RebuildFileLists(status);
            activity?.SetTag("wc.staged_count", _vm._allStaged.Count);
            activity?.SetTag("wc.unstaged_count", _vm._allUnstaged.Count);
            activity?.SetTag("wc.conflicted_count", _vm._allConflicted.Count);
            activity?.SetTag("wc.total_count", _vm._allStaged.Count + _vm._allUnstaged.Count + _vm._allConflicted.Count);
            activity?.SetTag("wc.visible_entry_count",
                _vm.StagedFileEntries.Count + _vm.UnstagedFileEntries.Count + _vm.ConflictedFileEntries.Count);
            ScheduleFileListLayoutTiming();
        }
        finally
        {
            GitDeltaMeters.WcFileListsRebuildMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public void RebuildFileLists(RepositoryStatus status)
    {
        // Preserve AI classifications across VM recreation so file-list icons survive status refresh.
        var classifications = CaptureAiClassifications();

        _vm._allStaged.Clear();
        _vm._allUnstaged.Clear();
        _vm._allConflicted.Clear();

        var pendingUnstage = new HashSet<string>(StringComparer.Ordinal);
        var pendingStage = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _vm._pending)
        {
            if (p.WasUnstage)
                pendingUnstage.Add(p.Path.Value);
            else
                pendingStage.Add(p.Path.Value);
        }

        foreach (var e in status.Staged)
        {
            if (pendingUnstage.Contains(e.Path.Value))
                continue;
            _vm._allStaged.Add(FileItemViewModel.From(e, isStagedList: true));
        }

        foreach (var e in status.Unstaged)
        {
            if (pendingStage.Contains(e.Path.Value))
                continue;
            _vm._allUnstaged.Add(FileItemViewModel.From(e, isStagedList: false));
        }

        var stagedPaths = _vm._allStaged.Select(f => f.Path.Value).ToHashSet(StringComparer.Ordinal);
        var unstagedPaths = _vm._allUnstaged.Select(f => f.Path.Value).ToHashSet(StringComparer.Ordinal);

        // Optimistic overlays: move predicted staged/unstaged
        foreach (var path in pendingStage)
        {
            if (!unstagedPaths.Contains(path) && !stagedPaths.Contains(path))
                _vm._allStaged.Add(new FileItemViewModel(FilePath.From(path), ChangeKind.Modified, isStagedList: true, isPartial: true, isOptimistic: true));
        }

        foreach (var path in pendingUnstage)
        {
            if (!unstagedPaths.Contains(path) && !stagedPaths.Contains(path))
                _vm._allUnstaged.Add(new FileItemViewModel(FilePath.From(path), ChangeKind.Modified, isStagedList: false, isPartial: true, isOptimistic: true));
        }

        foreach (var e in status.Conflicted)
            _vm._allConflicted.Add(FileItemViewModel.From(e, isStagedList: false));

        ApplyAiClassifications(classifications);

        _vm.WorkingCopyChangeCount = _vm._allStaged.Count + _vm._allUnstaged.Count + _vm._allConflicted.Count;
        _vm.ApplyFileFilter();
        var changePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in _vm._allStaged.Concat(_vm._allUnstaged).Concat(_vm._allConflicted))
            changePaths.Add(file.Path.Value);
        _vm.RecentViewedFiles.ExcludePaths(changePaths);
        _vm.OnPropertyChanged(nameof(WorkingCopyViewModel.HasRecentViewedFiles));
    }

    /// <summary>
    /// Measures time from file-list rebuild until the next UI layout/render pass
    /// (Avalonia realize + measure of file-list rows).
    /// </summary>
    public void ScheduleFileListLayoutTiming()
    {
        var parentContext = Activity.Current?.Context ?? default;
        var visibleCount = _vm.StagedFileEntries.Count + _vm.UnstagedFileEntries.Count + _vm.ConflictedFileEntries.Count;
        var sw = Stopwatch.StartNew();
        try
        {
            var dispatcher = Dispatcher.UIThread;
            void Record()
            {
                var ms = sw.Elapsed.TotalMilliseconds;
                GitDeltaMeters.WcFileListsLayoutMs.Record(ms);
                if (ms < FileListLayoutActivityMs)
                    return;

                using var activity = GitDeltaActivity.Source.StartActivity(
                    "wc.filelists.layout",
                    ActivityKind.Internal,
                    parentContext);
                activity?.SetTag("wc.visible_entry_count", visibleCount);
                activity?.SetTag("wc.layout_ms", ms);
            }

            if (Application.Current is null)
            {
                Record();
                return;
            }

            dispatcher.Post(Record, DispatcherPriority.Loaded);
        }
        catch (InvalidOperationException)
        {
            GitDeltaMeters.WcFileListsLayoutMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public Dictionary<string, AiChangeClassification> CaptureAiClassifications()
    {
        var map = new Dictionary<string, AiChangeClassification>(StringComparer.Ordinal);
        foreach (var file in _vm._allStaged.Concat(_vm._allUnstaged).Concat(_vm._allConflicted))
        {
            if (file.AiChangeClassification is { } classification)
                map.TryAdd(file.Path.Value, classification);
        }

        return map;
    }

    public void ApplyAiClassifications(Dictionary<string, AiChangeClassification> classifications)
    {
        if (classifications.Count == 0)
            return;

        foreach (var file in _vm._allStaged.Concat(_vm._allUnstaged).Concat(_vm._allConflicted))
        {
            if (classifications.TryGetValue(file.Path.Value, out var classification))
                file.AiChangeClassification = classification;
        }
    }

    public void ApplyOptimisticFileLists()
    {
        if (_vm._lastStatus is null) return;
        using var activity = GitDeltaActivity.Source.StartActivity("wc.stage.optimistic");
        var sw = Stopwatch.StartNew();
        try
        {
            RebuildFileListsTimed(_vm._lastStatus, "optimistic");
            activity?.SetTag(
                "wc.visible_entry_count",
                _vm.StagedFileEntries.Count + _vm.UnstagedFileEntries.Count + _vm.ConflictedFileEntries.Count);
        }
        finally
        {
            GitDeltaMeters.WcStageOptimisticMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }
    }
}

using System.Diagnostics;
using GitDelta.Core;
using GitDelta.Core.Diagnostics;

namespace GitDelta.App.ViewModels;

public partial class WorkingCopyViewModel
{
    /// <summary>
    /// Coalesces rapid checkbox stage/unstage toggles into batched git calls
    /// while applying optimistic list moves immediately.
    /// </summary>
    private sealed class WorkingCopyStageQueue(WorkingCopyViewModel vm)
    {
        private const int DebounceMs = 80;

        private readonly WorkingCopyViewModel _vm = vm;
        private readonly Dictionary<string, PendingMutation> _stagePending = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingMutation> _unstagePending = new(StringComparer.Ordinal);
        private CancellationTokenSource? _debounceCts;
        private int _flushGate; // 0 = idle, 1 = running
        private bool _flushAgain;

        public void EnqueueToggle(FileItemViewModel file)
        {
            if (_vm._repoPath is null)
                return;

            var pathKey = file.Path.Value;
            var wantUnstage = file.IsStagedList;

            if (wantUnstage)
            {
                if (_stagePending.Remove(pathKey, out var cancelled))
                {
                    _vm._pending.Remove(cancelled);
                    _vm.ApplyOptimisticFileLists();
                    ScheduleFlush();
                    return;
                }

                if (_unstagePending.ContainsKey(pathKey))
                    return;

                var pending = new PendingMutation(file.Path, WasUnstage: true);
                _unstagePending[pathKey] = pending;
                _vm._pending.Add(pending);
            }
            else
            {
                if (_unstagePending.Remove(pathKey, out var cancelled))
                {
                    _vm._pending.Remove(cancelled);
                    _vm.ApplyOptimisticFileLists();
                    ScheduleFlush();
                    return;
                }

                if (_stagePending.ContainsKey(pathKey))
                    return;

                var pending = new PendingMutation(file.Path, WasUnstage: false);
                _stagePending[pathKey] = pending;
                _vm._pending.Add(pending);
            }

            _vm.SoftInvalidatePathsTimed([file.Path]);
            _vm.ApplyOptimisticFileLists();
            ScheduleFlush();
        }

        private void ScheduleFlush()
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            _ = DebounceThenFlushAsync(cts);
        }

        private async Task DebounceThenFlushAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(DebounceMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!ReferenceEquals(_debounceCts, cts))
                return;

            await FlushAsync();
        }

        private async Task FlushAsync()
        {
            if (Interlocked.CompareExchange(ref _flushGate, 1, 0) != 0)
            {
                _flushAgain = true;
                return;
            }

            try
            {
                do
                {
                    _flushAgain = false;
                    await FlushOnceAsync();
                } while (_flushAgain || _stagePending.Count > 0 || _unstagePending.Count > 0);
            }
            finally
            {
                Interlocked.Exchange(ref _flushGate, 0);
                if (_flushAgain || _stagePending.Count > 0 || _unstagePending.Count > 0)
                    ScheduleFlush();
            }
        }

        private async Task FlushOnceAsync()
        {
            if (_vm._repoPath is null)
            {
                ClearAllPending();
                return;
            }

            var stageBatch = _stagePending.Values.ToList();
            var unstageBatch = _unstagePending.Values.ToList();
            _stagePending.Clear();
            _unstagePending.Clear();

            if (stageBatch.Count == 0 && unstageBatch.Count == 0)
                return;

            using var activity = GitDeltaActivity.Source.StartActivity("wc.stage.queue");
            activity?.SetTag("stage.op", "coalesced");
            activity?.SetTag("stage.file_count", stageBatch.Count + unstageBatch.Count);
            var sw = Stopwatch.StartNew();

            var mutated = stageBatch.Concat(unstageBatch).Select(p => p.Path).ToList();
            await _vm.YieldUiAfterOptimisticAsync();

            try
            {
                if (stageBatch.Count > 0)
                {
                    try
                    {
                        await _vm._staging.StageFilesAsync(
                            _vm._repoPath,
                            stageBatch.Select(p => p.Path).ToList());
                    }
                    catch (Exception ex)
                    {
                        _vm._notifications.Error(
                            $"Stage failed: {ex.Message}",
                            () => Requeue(stageBatch, unstage: false),
                            ex);
                    }
                }

                if (unstageBatch.Count > 0)
                {
                    try
                    {
                        await _vm._staging.UnstageFilesAsync(
                            _vm._repoPath,
                            unstageBatch.Select(p => p.Path).ToList());
                    }
                    catch (Exception ex)
                    {
                        _vm._notifications.Error(
                            $"Unstage failed: {ex.Message}",
                            () => Requeue(unstageBatch, unstage: true),
                            ex);
                    }
                }
            }
            finally
            {
                foreach (var pending in stageBatch.Concat(unstageBatch))
                    _vm._pending.Remove(pending);

                await _vm.RefreshAndMaybeReloadDiffAsync(mutated);
                GitDeltaMeters.WcStageMs.Record(sw.Elapsed.TotalMilliseconds);
            }
        }

        private void Requeue(IReadOnlyList<PendingMutation> batch, bool unstage)
        {
            foreach (var pending in batch)
            {
                var key = pending.Path.Value;
                if (unstage)
                {
                    if (_stagePending.ContainsKey(key) || _unstagePending.ContainsKey(key))
                        continue;
                    _unstagePending[key] = pending;
                }
                else
                {
                    if (_stagePending.ContainsKey(key) || _unstagePending.ContainsKey(key))
                        continue;
                    _stagePending[key] = pending;
                }

                if (!_vm._pending.Contains(pending))
                    _vm._pending.Add(pending);
            }

            _vm.ApplyOptimisticFileLists();
            ScheduleFlush();
        }

        private void ClearAllPending()
        {
            foreach (var pending in _stagePending.Values.Concat(_unstagePending.Values))
                _vm._pending.Remove(pending);
            _stagePending.Clear();
            _unstagePending.Clear();
            _vm.ApplyOptimisticFileLists();
        }
    }
}

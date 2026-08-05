using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.App.ViewModels;

public enum RebaseWizardStep
{
    SelectBase,
    EditPlan,
    Running,
    Conflicts,
    Review,
}

/// <summary>One commit row in the rebase plan (reorder + action + optional message).</summary>
public partial class RebaseTodoItemViewModel : ObservableObject
{
    public RebaseTodoItemViewModel(CommitInfo commit, CommitStat? stat = null)
    {
        Commit = commit;
        Stat = stat;
        Message = string.IsNullOrWhiteSpace(commit.Body)
            ? commit.Subject
            : $"{commit.Subject}\n\n{commit.Body}".TrimEnd();
    }

    public CommitInfo Commit { get; }

    [ObservableProperty] private CommitStat? _stat;
    [ObservableProperty] private RebaseTodoAction _action = RebaseTodoAction.Pick;
    [ObservableProperty] private string _message;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private IReadOnlyList<(FilePath Path, ChangeKind Kind)> _files =
        Array.Empty<(FilePath, ChangeKind)>();
    [ObservableProperty] private bool _filesLoaded;

    public string ShortOid => Commit.ShortOid;
    public string Subject => Commit.Subject;
    public string AuthorDisplay => Commit.AuthorDisplay;
    public string AuthorDateDisplay => Commit.AuthorDate.ToLocalTime().ToString("g");
    public string StatDisplay =>
        Stat is null
            ? "…"
            : $"{Stat.FileCount} files, +{Stat.Insertions} −{Stat.Deletions}";

    public IReadOnlyList<string> FilePathDisplays =>
        Files.Select(f => f.Path.Value).ToList();

    public bool NeedsMessage =>
        Action is RebaseTodoAction.Reword or RebaseTodoAction.Squash;

    public IReadOnlyList<RebaseTodoAction> AvailableActions { get; } =
    [
        RebaseTodoAction.Pick,
        RebaseTodoAction.Reword,
        RebaseTodoAction.Squash,
        RebaseTodoAction.Fixup,
        RebaseTodoAction.Drop,
    ];

    partial void OnActionChanged(RebaseTodoAction value) =>
        OnPropertyChanged(nameof(NeedsMessage));

    partial void OnStatChanged(CommitStat? value) =>
        OnPropertyChanged(nameof(StatDisplay));

    partial void OnFilesChanged(IReadOnlyList<(FilePath Path, ChangeKind Kind)> value) =>
        OnPropertyChanged(nameof(FilePathDisplays));
}

/// <summary>Before/after review row with diffstat.</summary>
public sealed class RebaseReviewCommitViewModel
{
    public RebaseReviewCommitViewModel(CommitInfo commit, CommitStat? stat)
    {
        Commit = commit;
        Stat = stat;
    }

    public CommitInfo Commit { get; }
    public CommitStat? Stat { get; }
    public string ShortOid => Commit.ShortOid;
    public string Subject => Commit.Subject;
    public string AuthorDisplay => Commit.AuthorDisplay;
    public string StatDisplay =>
        Stat is null
            ? "—"
            : $"{Stat.FileCount} files, +{Stat.Insertions} −{Stat.Deletions}";
}

/// <summary>
/// Multi-step interactive rebase wizard: choose base → edit todo → run/resolve → review → optional
/// force-with-lease push.
/// </summary>
public partial class RebaseWizardViewModel : ObservableObject
{
    private readonly IGitBranchService _branches;
    private readonly IGitHistoryService _history;
    private readonly IGitRebaseService _rebase;
    private readonly IGitStashService _stash;
    private readonly IConfirmDialog _confirm;
    private readonly NotificationService _notifications;
    private readonly Func<int> _getChangeCount;
    private readonly Func<Task> _refreshHostAsync;

    private string? _repoPath;
    private string? _ontoRef;
    private string? _currentBranch;
    private string? _upstream;
    private List<RebaseReviewCommitViewModel> _beforeSnapshot = [];

    public RebaseWizardViewModel(
        IGitBranchService branches,
        IGitHistoryService history,
        IGitRebaseService rebase,
        IGitStashService stash,
        IConfirmDialog confirm,
        NotificationService notifications,
        Func<int> getChangeCount,
        Func<Task> refreshHostAsync)
    {
        _branches = branches;
        _history = history;
        _rebase = rebase;
        _stash = stash;
        _confirm = confirm;
        _notifications = notifications;
        _getChangeCount = getChangeCount;
        _refreshHostAsync = refreshHostAsync;

        TodoItems.CollectionChanged += OnTodoCollectionChanged;
    }

    public ObservableCollection<BranchInfo> BaseBranches { get; } = [];
    public ObservableCollection<RebaseTodoItemViewModel> TodoItems { get; } = [];
    public ObservableCollection<RebaseReviewCommitViewModel> BeforeCommits { get; } = [];
    public ObservableCollection<RebaseReviewCommitViewModel> AfterCommits { get; } = [];

    public static IReadOnlyList<RebaseTodoAction> TodoActions { get; } =
    [
        RebaseTodoAction.Pick,
        RebaseTodoAction.Reword,
        RebaseTodoAction.Squash,
        RebaseTodoAction.Fixup,
        RebaseTodoAction.Drop,
    ];

    [ObservableProperty] private bool _ownsInProgressRebase;
    [ObservableProperty] private RebaseWizardStep _step = RebaseWizardStep.SelectBase;
    [ObservableProperty] private BranchInfo? _selectedBaseBranch;
    [ObservableProperty] private RebaseTodoItemViewModel? _selectedTodoItem;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _hasUpstream;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _conflictDetail;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private int _beforeTotalFiles;
    [ObservableProperty] private int _beforeTotalInsertions;
    [ObservableProperty] private int _beforeTotalDeletions;
    [ObservableProperty] private int _afterTotalFiles;
    [ObservableProperty] private int _afterTotalInsertions;
    [ObservableProperty] private int _afterTotalDeletions;

    public bool IsSelectBaseStep => Step == RebaseWizardStep.SelectBase;
    public bool IsEditPlanStep => Step == RebaseWizardStep.EditPlan;
    public bool IsRunningStep => Step == RebaseWizardStep.Running;
    public bool IsConflictsStep => Step == RebaseWizardStep.Conflicts;
    public bool IsReviewStep => Step == RebaseWizardStep.Review;

    public string StepTitle => Step switch
    {
        RebaseWizardStep.SelectBase => "Choose base branch",
        RebaseWizardStep.EditPlan => "Edit rebase plan",
        RebaseWizardStep.Running => "Rebasing…",
        RebaseWizardStep.Conflicts => "Resolve conflicts",
        RebaseWizardStep.Review => "Review result",
        _ => "Rebase",
    };

    public string BeforeSummary =>
        $"{BeforeCommits.Count} commits · {BeforeTotalFiles} files · +{BeforeTotalInsertions} −{BeforeTotalDeletions}";

    public string AfterSummary =>
        $"{AfterCommits.Count} commits · {AfterTotalFiles} files · +{AfterTotalInsertions} −{AfterTotalDeletions}";

    partial void OnStepChanged(RebaseWizardStep value)
    {
        OnPropertyChanged(nameof(IsSelectBaseStep));
        OnPropertyChanged(nameof(IsEditPlanStep));
        OnPropertyChanged(nameof(IsRunningStep));
        OnPropertyChanged(nameof(IsConflictsStep));
        OnPropertyChanged(nameof(IsReviewStep));
        OnPropertyChanged(nameof(StepTitle));
    }

    partial void OnBeforeTotalFilesChanged(int value) => OnPropertyChanged(nameof(BeforeSummary));
    partial void OnBeforeTotalInsertionsChanged(int value) => OnPropertyChanged(nameof(BeforeSummary));
    partial void OnBeforeTotalDeletionsChanged(int value) => OnPropertyChanged(nameof(BeforeSummary));
    partial void OnAfterTotalFilesChanged(int value) => OnPropertyChanged(nameof(AfterSummary));
    partial void OnAfterTotalInsertionsChanged(int value) => OnPropertyChanged(nameof(AfterSummary));
    partial void OnAfterTotalDeletionsChanged(int value) => OnPropertyChanged(nameof(AfterSummary));

    public async Task OpenAsync(string repositoryPath, string? currentBranch, string? upstream)
    {
        _repoPath = repositoryPath;
        _currentBranch = currentBranch;
        _upstream = upstream;
        OwnsInProgressRebase = false;
        _ontoRef = null;
        _beforeSnapshot = [];
        HasUpstream = !string.IsNullOrWhiteSpace(upstream);
        ValidationError = null;
        ConflictDetail = null;
        StatusMessage = null;
        Step = RebaseWizardStep.SelectBase;
        IsDirty = _getChangeCount() > 0;

        TodoItems.Clear();
        BeforeCommits.Clear();
        AfterCommits.Clear();
        SelectedTodoItem = null;

        await LoadBranchesAndCommitsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Attempts to close the wizard. Returns false if the user cancelled a mid-rebase abort warning.
    /// </summary>
    public async Task<bool> RequestCloseAsync()
    {
        if (OwnsInProgressRebase || Step is RebaseWizardStep.Running or RebaseWizardStep.Conflicts)
        {
            var confirmed = await _confirm.ConfirmAsync(
                "Cancel rebase?",
                "Closing will abort the in-progress rebase and restore the branch to its previous state.",
                "Abort rebase").ConfigureAwait(true);
            if (!confirmed)
                return false;

            await AbortInternalAsync().ConfigureAwait(true);
        }

        Reset();
        return true;
    }

    public void Reset()
    {
        _repoPath = null;
        _ontoRef = null;
        OwnsInProgressRebase = false;
        TodoItems.Clear();
        BeforeCommits.Clear();
        AfterCommits.Clear();
        BaseBranches.Clear();
        SelectedBaseBranch = null;
        SelectedTodoItem = null;
        Step = RebaseWizardStep.SelectBase;
        StatusMessage = null;
        ConflictDetail = null;
        ValidationError = null;
    }

    [RelayCommand]
    private async Task RefreshDirtyStateAsync()
    {
        IsDirty = _getChangeCount() > 0;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task StashChangesAsync()
    {
        if (_repoPath is null || IsBusy) return;
        IsBusy = true;
        try
        {
            await _stash.StashPushAsync(_repoPath, "CodeReviewr rebase wizard", includeUntracked: true)
                .ConfigureAwait(true);
            await _refreshHostAsync().ConfigureAwait(true);
            IsDirty = _getChangeCount() > 0;
            StatusMessage = IsDirty ? "Stash completed, but the worktree is still dirty." : "Changes stashed.";
            _notifications.Info("Stashed local changes for rebase");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Stash failed: {ex.Message}";
            _notifications.Error($"Stash failed: {ex.Message}", null, ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ContinueToPlanAsync()
    {
        if (SelectedBaseBranch is null)
        {
            ValidationError = "Select a base branch.";
            return;
        }

        if (IsDirty)
        {
            ValidationError = "Stash or discard local changes before continuing.";
            return;
        }

        if (TodoItems.Count == 0)
        {
            ValidationError = "There are no commits ahead of the selected base.";
            return;
        }

        ValidationError = null;
        _ontoRef = SelectedBaseBranch.Name;
        Step = RebaseWizardStep.EditPlan;
        SelectedTodoItem ??= TodoItems.FirstOrDefault();
        if (SelectedTodoItem is not null)
            await EnsureTodoDetailsAsync(SelectedTodoItem).ConfigureAwait(true);
    }

    [RelayCommand]
    private void BackToBase()
    {
        if (IsBusy) return;
        ValidationError = null;
        Step = RebaseWizardStep.SelectBase;
    }

    [RelayCommand]
    private async Task SelectTodoAsync(RebaseTodoItemViewModel? item)
    {
        if (item is null) return;
        SelectedTodoItem = item;
        foreach (var row in TodoItems)
            row.IsSelected = ReferenceEquals(row, item);
        await EnsureTodoDetailsAsync(item).ConfigureAwait(true);
    }

    [RelayCommand]
    private void MoveTodoUp(RebaseTodoItemViewModel? item)
    {
        if (item is null) return;
        var index = TodoItems.IndexOf(item);
        if (index <= 0) return;
        TodoItems.Move(index, index - 1);
        ValidatePlan();
    }

    [RelayCommand]
    private void MoveTodoDown(RebaseTodoItemViewModel? item)
    {
        if (item is null) return;
        var index = TodoItems.IndexOf(item);
        if (index < 0 || index >= TodoItems.Count - 1) return;
        TodoItems.Move(index, index + 1);
        ValidatePlan();
    }

    [RelayCommand]
    private async Task StartRebaseAsync()
    {
        if (_repoPath is null || string.IsNullOrWhiteSpace(_ontoRef) || IsBusy) return;
        if (!ValidatePlan()) return;

        IsBusy = true;
        Step = RebaseWizardStep.Running;
        StatusMessage = "Starting interactive rebase…";

        try
        {
            _beforeSnapshot = await CaptureReviewAsync(_ontoRef).ConfigureAwait(true);
            BeforeCommits.Clear();
            foreach (var row in _beforeSnapshot)
                BeforeCommits.Add(row);
            Summarize(BeforeCommits, out var files, out var ins, out var del);
            BeforeTotalFiles = files;
            BeforeTotalInsertions = ins;
            BeforeTotalDeletions = del;

            var todo = TodoItems.Select(ToEntry).ToList();
            var result = await _rebase.StartInteractiveAsync(_repoPath, _ontoRef, todo).ConfigureAwait(true);
            await HandleRunResultAsync(result).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            OwnsInProgressRebase = false;
            Step = RebaseWizardStep.EditPlan;
            StatusMessage = $"Rebase failed: {ex.Message}";
            _notifications.Error($"Rebase failed: {ex.Message}", null, ex);
            await _refreshHostAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResumeRebaseAsync()
    {
        if (_repoPath is null || IsBusy) return;
        IsBusy = true;
        StatusMessage = "Continuing rebase…";
        try
        {
            var result = await _rebase.ContinueAsync(_repoPath).ConfigureAwait(true);
            await HandleRunResultAsync(result).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Continue failed: {ex.Message}";
            _notifications.Error($"Continue failed: {ex.Message}", null, ex);
            await _refreshHostAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AbortRebaseAsync()
    {
        if (_repoPath is null || IsBusy) return;
        var confirmed = await _confirm.ConfirmAsync(
            "Abort rebase?",
            "This restores the branch to its state before the rebase started.",
            "Abort rebase").ConfigureAwait(true);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            await AbortInternalAsync().ConfigureAwait(true);
            Step = RebaseWizardStep.EditPlan;
            StatusMessage = "Rebase aborted.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedBaseBranchChanged(BranchInfo? value)
    {
        if (value is null || _repoPath is null || Step != RebaseWizardStep.SelectBase)
            return;
        _ = ReloadCommitsForBaseAsync(value.Name);
    }

    partial void OnSelectedTodoItemChanged(RebaseTodoItemViewModel? value)
    {
        foreach (var row in TodoItems)
            row.IsSelected = ReferenceEquals(row, value);
        if (value is not null)
            _ = EnsureTodoDetailsAsync(value);
    }

    private async Task LoadBranchesAndCommitsAsync()
    {
        if (_repoPath is null) return;
        IsBusy = true;
        try
        {
            var listed = await _branches.ListBranchesAsync(_repoPath).ConfigureAwait(true);
            BaseBranches.Clear();
            foreach (var b in listed)
            {
                if (string.Equals(b.Name, _currentBranch, StringComparison.Ordinal))
                    continue;
                BaseBranches.Add(b);
            }

            SelectedBaseBranch = PickDefaultBase(BaseBranches, _upstream);
            if (SelectedBaseBranch is not null)
                await ReloadCommitsForBaseAsync(SelectedBaseBranch.Name).ConfigureAwait(true);
            else
            {
                TodoItems.Clear();
                StatusMessage = "No suitable base branch found.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load branches: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadCommitsForBaseAsync(string baseRef)
    {
        if (_repoPath is null) return;
        try
        {
            var commits = await _history.ListCommitsRangeAsync(
                _repoPath, baseRef, "HEAD", oldestFirst: true).ConfigureAwait(true);

            TodoItems.Clear();
            foreach (var commit in commits)
                TodoItems.Add(new RebaseTodoItemViewModel(commit));

            SelectedTodoItem = TodoItems.FirstOrDefault();
            if (SelectedTodoItem is not null)
            {
                SelectedTodoItem.IsSelected = true;
                _ = EnsureTodoDetailsAsync(SelectedTodoItem);
            }

            // Warm stats for the list (best-effort, parallel-ish sequential to avoid gate contention).
            foreach (var item in TodoItems.ToList())
            {
                try
                {
                    item.Stat = await _history.GetCommitStatAsync(_repoPath, item.Commit.Oid)
                        .ConfigureAwait(true);
                }
                catch
                {
                    // Leave Stat null; UI shows ellipsis.
                }
            }

            StatusMessage = TodoItems.Count == 0
                ? $"No commits on the current branch that are not on {baseRef}."
                : $"{TodoItems.Count} commit(s) ahead of {baseRef}.";
            ValidationError = null;
        }
        catch (Exception ex)
        {
            TodoItems.Clear();
            StatusMessage = $"Failed to load commits: {ex.Message}";
        }
    }

    private async Task EnsureTodoDetailsAsync(RebaseTodoItemViewModel item)
    {
        if (_repoPath is null || item.FilesLoaded) return;
        try
        {
            item.Files = await _history.GetCommitFilesAsync(_repoPath, item.Commit.Oid).ConfigureAwait(true);
            item.Stat ??= await _history.GetCommitStatAsync(_repoPath, item.Commit.Oid).ConfigureAwait(true);
            item.FilesLoaded = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load commit details: {ex.Message}";
        }
    }

    private async Task HandleRunResultAsync(RebaseRunResult result)
    {
        await _refreshHostAsync().ConfigureAwait(true);

        if (result.Outcome == RebaseRunOutcome.Conflicts)
        {
            OwnsInProgressRebase = true;
            Step = RebaseWizardStep.Conflicts;
            ConflictDetail = result.Detail;
            StatusMessage = "Rebase paused on conflicts.";
            return;
        }

        OwnsInProgressRebase = false;
        await LoadReviewAfterAsync().ConfigureAwait(true);
        Step = RebaseWizardStep.Review;
        StatusMessage = "Rebase completed.";
    }

    private async Task LoadReviewAfterAsync()
    {
        if (_repoPath is null || string.IsNullOrWhiteSpace(_ontoRef)) return;

        AfterCommits.Clear();
        var after = await CaptureReviewAsync(_ontoRef).ConfigureAwait(true);
        foreach (var row in after)
            AfterCommits.Add(row);
        Summarize(AfterCommits, out var files, out var ins, out var del);
        AfterTotalFiles = files;
        AfterTotalInsertions = ins;
        AfterTotalDeletions = del;
    }

    private async Task<List<RebaseReviewCommitViewModel>> CaptureReviewAsync(string baseRef)
    {
        var list = new List<RebaseReviewCommitViewModel>();
        if (_repoPath is null) return list;

        var commits = await _history.ListCommitsRangeAsync(
            _repoPath, baseRef, "HEAD", oldestFirst: false).ConfigureAwait(true);
        foreach (var commit in commits)
        {
            CommitStat? stat = null;
            try
            {
                stat = await _history.GetCommitStatAsync(_repoPath, commit.Oid).ConfigureAwait(true);
            }
            catch
            {
                // optional
            }

            list.Add(new RebaseReviewCommitViewModel(commit, stat));
        }

        return list;
    }

    private async Task AbortInternalAsync()
    {
        if (_repoPath is null) return;
        try
        {
            await _rebase.AbortAsync(_repoPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Abort failed: {ex.Message}", null, ex);
        }

        OwnsInProgressRebase = false;
        await _refreshHostAsync().ConfigureAwait(true);
    }

    private bool ValidatePlan()
    {
        ValidationError = null;
        var kept = TodoItems.Where(t => t.Action != RebaseTodoAction.Drop).ToList();
        if (kept.Count == 0)
        {
            ValidationError = "Keep at least one commit (cannot drop all).";
            return false;
        }

        if (kept[0].Action is RebaseTodoAction.Squash or RebaseTodoAction.Fixup)
        {
            ValidationError = "The first kept commit cannot be squash or fixup.";
            return false;
        }

        foreach (var item in kept)
        {
            if (item.NeedsMessage && string.IsNullOrWhiteSpace(item.Message))
            {
                ValidationError = $"Commit {item.ShortOid} needs a commit message for {item.Action}.";
                return false;
            }
        }

        return true;
    }

    private void OnTodoCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (RebaseTodoItemViewModel item in e.NewItems)
                item.PropertyChanged += OnTodoItemPropertyChanged;
        }

        if (e.OldItems is not null)
        {
            foreach (RebaseTodoItemViewModel item in e.OldItems)
                item.PropertyChanged -= OnTodoItemPropertyChanged;
        }
    }

    private void OnTodoItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RebaseTodoItemViewModel.Action)
            or nameof(RebaseTodoItemViewModel.Message))
        {
            ValidatePlan();
        }
    }

    private static RebaseTodoEntry ToEntry(RebaseTodoItemViewModel item) =>
        new(item.Commit.Oid, item.Action, item.NeedsMessage ? item.Message : null);

    private static void Summarize(
        IReadOnlyList<RebaseReviewCommitViewModel> rows,
        out int files,
        out int insertions,
        out int deletions)
    {
        files = 0;
        insertions = 0;
        deletions = 0;
        foreach (var row in rows)
        {
            if (row.Stat is null) continue;
            files += row.Stat.FileCount;
            insertions += row.Stat.Insertions;
            deletions += row.Stat.Deletions;
        }
    }

    public static BranchInfo? PickDefaultBase(IEnumerable<BranchInfo> branches, string? upstream)
    {
        var list = branches.ToList();
        if (list.Count == 0) return null;

        var originHead = list.FirstOrDefault(b =>
            string.Equals(b.Name, "origin/HEAD", StringComparison.OrdinalIgnoreCase));
        if (originHead is not null) return originHead;

        if (!string.IsNullOrWhiteSpace(upstream))
        {
            var byUpstream = list.FirstOrDefault(b =>
                string.Equals(b.Name, upstream, StringComparison.OrdinalIgnoreCase));
            if (byUpstream is not null) return byUpstream;
        }

        foreach (var candidate in new[] { "main", "origin/main", "master", "origin/master" })
        {
            var match = list.FirstOrDefault(b =>
                string.Equals(b.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return list[0];
    }

    public static bool IsProtectedBranchName(string? branch) =>
        string.Equals(branch, "main", StringComparison.OrdinalIgnoreCase)
        || string.Equals(branch, "master", StringComparison.OrdinalIgnoreCase);

    public static string BuildForcePushGuidance(GitException ex)
    {
        var detail = ex.StderrSummary ?? ex.Message;
        if (detail.Contains("stale info", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("failed to push some refs", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("cannot lock ref", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("force-with-lease", StringComparison.OrdinalIgnoreCase))
        {
            return "Force-with-lease failed because the remote branch has moved since your last fetch. "
                + "Fetch from origin, confirm nobody else pushed commits you still need, then try again. "
                + "If you intentionally want to overwrite the remote tip, fetch first so lease can succeed against the latest remote OID.";
        }

        return $"Force-with-lease push failed: {detail}";
    }
}

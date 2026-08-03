namespace CodeReviewr.Core;

public enum DiffTarget
{
    /// <summary>git diff — unstaged changes</summary>
    IndexToWorktree,
    /// <summary>git diff --cached — staged changes</summary>
    HeadToIndex,
    /// <summary>git diff HEAD — combined review mode (read-only for staging)</summary>
    HeadToWorktree,
}

public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Ignored,
    Conflicted,
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    NoNewlineAtEof,
}

public enum DiffSide
{
    Old,
    New,
}

public enum DiffViewMode
{
    Unified,
    SideBySide,
}

public enum InProgressOperation
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
}

public enum CancellationClass
{
    /// <summary>diff, status, log, cat-file, push — kill the process.</summary>
    FreelyKillable,
    /// <summary>Index writes — remove from queue if not started; once spawned, let finish.</summary>
    CancellableAtDispatchOnly,
    /// <summary>checkout, pull, merge — request abort, then run defined recovery.</summary>
    AbortableNotCancellable,
}

/// <summary>Which middle/right pane content the sidebar currently drives.</summary>
public enum WorkspaceMode
{
    FileStatus,
    History,
    Stash,
    PullRequest,
}

/// <summary>Viewed-state filter for pull request file lists.</summary>
public enum ViewedFilter
{
    All,
    Viewed,
    NotViewed,
}

/// <summary>Layout for changed-file sidebars (flat paths vs folder tree vs AI order).</summary>
public enum FileListLayoutMode
{
    Flat,
    Tree,
    AiSuggested,
}

namespace GitDelta.App.ViewModels;

/// <summary>How a historical commit is compared while in file-history browse mode.</summary>
public enum FileHistoryCompareMode
{
    /// <summary>Parent vs the selected commit (the change introduced by that commit).</summary>
    InCommit = 0,

    /// <summary>Selected commit vs the current file (worktree or PR head).</summary>
    VsCurrent = 1,
}

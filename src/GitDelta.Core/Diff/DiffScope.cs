namespace GitDelta.Core.Diff;

/// <summary>
/// Describes what two states a <see cref="FileDiff"/> compares. Working-copy scopes map to
/// <see cref="DiffTarget"/>; revision scopes compare commits (three-dot or two-dot) or a
/// revision against the worktree.
/// </summary>
public abstract record DiffScope
{
    public sealed record WorkingCopy(DiffTarget Target) : DiffScope;

    /// <summary>Three-dot <c>base...head</c> (merge-base of base and head → head).</summary>
    public sealed record Revisions(CommitId Base, CommitId Head) : DiffScope;

    /// <summary>Two-dot <c>base head</c> (exact tree of base → exact tree of head).</summary>
    public sealed record RevisionsTwoDot(CommitId Base, CommitId Head) : DiffScope;

    /// <summary><c>git diff revision -- path</c> (revision blob → worktree).</summary>
    public sealed record RevisionToWorktree(CommitId Revision) : DiffScope;
}

public static class DiffScopeExtensions
{
    public static DiffScope.WorkingCopy AsWorkingCopy(this DiffTarget target) => new(target);

    public static bool TryGetWorkingCopyTarget(this DiffScope scope, out DiffTarget target)
    {
        if (scope is DiffScope.WorkingCopy wc)
        {
            target = wc.Target;
            return true;
        }

        target = default;
        return false;
    }

    public static DiffTarget? WorkingCopyTargetOrNull(this DiffScope scope) =>
        scope is DiffScope.WorkingCopy wc ? wc.Target : null;
}

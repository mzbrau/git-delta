namespace GitDelta.Core.Diff;

/// <summary>
/// Describes what two states a <see cref="FileDiff"/> compares. Working-copy scopes map to
/// <see cref="DiffTarget"/>; revision scopes compare two commits via three-dot diff syntax.
/// </summary>
public abstract record DiffScope
{
    public sealed record WorkingCopy(DiffTarget Target) : DiffScope;

    public sealed record Revisions(CommitId Base, CommitId Head) : DiffScope;
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

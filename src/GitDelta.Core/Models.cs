namespace GitDelta.Core;

public sealed record StatusEntry(
    FilePath Path,
    FilePath? OriginalPath,
    ChangeKind Kind,
    bool IsStaged,
    bool IsUnstaged,
    bool IsConflicted,
    ContentId? IndexOid = null,
    ContentId? WorktreeOid = null,
    ContentId? HeadOid = null);

public sealed record RepositoryStatus(
    IReadOnlyList<StatusEntry> Staged,
    IReadOnlyList<StatusEntry> Unstaged,
    IReadOnlyList<StatusEntry> Conflicted,
    InProgressOperation InProgress,
    string? CurrentBranch,
    long Epoch);

public sealed record BranchInfo(
    string Name,
    bool IsCurrent,
    bool IsRemote,
    string? Upstream,
    string TipOid);

public sealed record StashInfo(
    int Index,
    string Message,
    string? BranchHint)
{
    public string Ref => $"stash@{{{Index}}}";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Message) ? Ref : Message;
}

public sealed record CommitInfo(
    string Oid,
    string ShortOid,
    string Subject,
    string Body,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    IReadOnlyList<string> ParentOids,
    IReadOnlyList<string> Decorations)
{
    public bool IsRoot => ParentOids.Count == 0;

    public string AuthorDisplay =>
        string.IsNullOrWhiteSpace(AuthorEmail) ? AuthorName : $"{AuthorName} <{AuthorEmail}>";

    public string DecorationsDisplay =>
        Decorations.Count == 0 ? "" : string.Join(", ", Decorations);
}

/// <summary>Per-commit diffstat from <c>git show --numstat</c>.</summary>
public sealed record CommitStat(
    string Oid,
    int FileCount,
    int Insertions,
    int Deletions);

/// <summary>One line of a prepared interactive-rebase todo.</summary>
public sealed record RebaseTodoEntry(
    string Oid,
    RebaseTodoAction Action,
    string? Message = null);

/// <summary>Result of <c>rebase -i</c> start or continue.</summary>
public sealed record RebaseRunResult(RebaseRunOutcome Outcome, string? Detail = null);

public sealed record DiscardedEntry(
    FilePath Path,
    ContentId ObjectId,
    DateTimeOffset DiscardedAt,
    bool WasUntracked);

public sealed record GitVersion(int Major, int Minor, int Patch, string Raw)
{
    public static readonly GitVersion Minimum = new(2, 30, 0, "2.30.0");

    public bool MeetsMinimum =>
        Major > Minimum.Major
        || (Major == Minimum.Major && Minor > Minimum.Minor)
        || (Major == Minimum.Major && Minor == Minimum.Minor && Patch >= Minimum.Patch);

    public override string ToString() => Raw;
}

public sealed record GitExecutableInfo(string Path, GitVersion Version);

public sealed record GitResult(int ExitCode, string? StderrSummary)
{
    public bool Succeeded => ExitCode == 0;
}

public class GitException : Exception
{
    public int ExitCode { get; }
    public string? StderrSummary { get; }
    public bool IsAuthFailure { get; }
    public bool IsIndexLocked { get; }

    public GitException(string message, int exitCode = -1, string? stderr = null,
        bool isAuthFailure = false, bool isIndexLocked = false)
        : base(message)
    {
        ExitCode = exitCode;
        StderrSummary = stderr;
        IsAuthFailure = isAuthFailure;
        IsIndexLocked = isIndexLocked;
    }
}

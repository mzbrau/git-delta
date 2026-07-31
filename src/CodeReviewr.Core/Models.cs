namespace CodeReviewr.Core;

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

public sealed class GitException : Exception
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

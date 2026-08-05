using CodeReviewr.Core.Diff;

namespace CodeReviewr.Core.Abstractions;

public interface IGitEnvironment
{
    GitExecutableInfo? Current { get; }
    Task<GitExecutableInfo> DetectAsync(CancellationToken ct = default);
    void SetOverridePath(string? path);
}

public interface IGitStatusService
{
    Task<RepositoryStatus> GetStatusAsync(string repositoryPath, CancellationToken ct = default);
}

public interface IGitDiffService
{
    Task<FileDiff> GetDiffAsync(
        string repositoryPath,
        FilePath path,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default);

    Task<IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)>> GetRawDiffAsync(
        string repositoryPath,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Low-level source of Git's own diff output. The only Phase 1 implementation shells out to
/// `git diff` via CliWrap; <see cref="IGitDiffService"/> is the orchestrator built on top of it
/// that adds parsing (<c>PatchParser</c>) and content-addressed caching (<see cref="IDiffCache"/>).
/// </summary>
public interface IGitDiffRawService
{
    /// <summary>
    /// Returns the raw unified diff patch text for a single file, exactly as Git produces it
    /// (verbatim, including headers), ready to be retained as <see cref="FileDiff.RawPatch"/>.
    /// </summary>
    Task<string> GetPatchAsync(
        string repositoryPath,
        FilePath path,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the cheap file-level summary (`git diff --raw`), including both blob object ids,
    /// used to populate file lists and to resolve content identity without parsing full patches.
    /// </summary>
    Task<IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)>> GetRawFileListAsync(
        string repositoryPath,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default);
}

public interface IGitObjectReader : IAsyncDisposable
{
    Task<byte[]> ReadBlobAsync(string repositoryPath, ContentId oid, CancellationToken ct = default);
    Task<ContentId> HashObjectAsync(string repositoryPath, string filePath, bool write, CancellationToken ct = default);
}

public interface IGitStagingService
{
    Task StageFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default);
    Task UnstageFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default);
    Task StageFilesAsync(string repositoryPath, IReadOnlyList<FilePath> paths, CancellationToken ct = default);
    Task UnstageFilesAsync(string repositoryPath, IReadOnlyList<FilePath> paths, CancellationToken ct = default);
    Task StagePatchAsync(string repositoryPath, string patch, CancellationToken ct = default);
    Task UnstagePatchAsync(string repositoryPath, string patch, CancellationToken ct = default);
}

public interface IGitDiscardService
{
    Task DiscardFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default);
    /// <summary>Restores index and worktree to HEAD for a staged (or partially staged) path.</summary>
    Task DiscardStagedFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default);
    Task DiscardPatchAsync(string repositoryPath, string patch, CancellationToken ct = default);
    Task RestoreDiscardedAsync(string repositoryPath, DiscardedEntry entry, CancellationToken ct = default);
    IReadOnlyList<DiscardedEntry> RecentlyDiscarded { get; }
}

public interface IGitCommitService
{
    Task CommitAsync(
        string repositoryPath,
        string message,
        bool amend,
        bool noVerify,
        IProgress<string>? hookOutput,
        CancellationToken ct = default);
}

public interface IGitBranchService
{
    Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string repositoryPath, CancellationToken ct = default);
    Task CheckoutAsync(string repositoryPath, string branch, CancellationToken ct = default);
    Task CreateBranchAsync(string repositoryPath, string name, bool checkout, CancellationToken ct = default);
    Task DeleteBranchAsync(string repositoryPath, string name, bool force, CancellationToken ct = default);
    Task RenameBranchAsync(string repositoryPath, string oldName, string newName, CancellationToken ct = default);
    Task FetchAsync(string repositoryPath, CancellationToken ct = default);
}

public interface IGitRemoteService
{
    Task PushAsync(string repositoryPath, IProgress<string>? progress, CancellationToken ct = default);
    Task ForcePushWithLeaseAsync(string repositoryPath, IProgress<string>? progress, CancellationToken ct = default);
    Task PullAsync(string repositoryPath, PullMode mode, IProgress<string>? progress, CancellationToken ct = default);
    Task<string?> GetRemoteUrlAsync(string repositoryPath, string remoteName = "origin", CancellationToken ct = default);
}

/// <summary>Interactive rebase start via prepared todo + <c>GIT_SEQUENCE_EDITOR</c>.</summary>
public interface IGitRebaseService
{
    /// <summary>
    /// Starts <c>git rebase -i</c> onto <paramref name="ontoRef"/> using the given todo.
    /// Dropped entries are omitted; reword/squash messages are fed via a custom <c>GIT_EDITOR</c>.
    /// </summary>
    Task<RebaseRunResult> StartInteractiveAsync(
        string repositoryPath,
        string ontoRef,
        IReadOnlyList<RebaseTodoEntry> todo,
        CancellationToken ct = default);

    Task<RebaseRunResult> ContinueAsync(string repositoryPath, CancellationToken ct = default);
    Task AbortAsync(string repositoryPath, CancellationToken ct = default);
}

public enum PullMode
{
    FfOnly,
    Merge,
    Rebase,
}

public interface IGitCloneService
{
    Task CloneAsync(string url, string targetDirectory, IProgress<string>? progress, CancellationToken ct = default);
}

public interface IGitConflictService
{
    Task<InProgressOperation> DetectInProgressAsync(string repositoryPath, CancellationToken ct = default);
    Task AbortAsync(string repositoryPath, CancellationToken ct = default);
    Task ContinueAsync(string repositoryPath, CancellationToken ct = default);
    Task OpenMergetoolAsync(string repositoryPath, FilePath? path, CancellationToken ct = default);
    Task MarkResolvedAsync(string repositoryPath, FilePath path, CancellationToken ct = default);
}

public interface IGitStashService
{
    Task<IReadOnlyList<StashInfo>> ListStashesAsync(string repositoryPath, CancellationToken ct = default);
    Task StashPushAsync(string repositoryPath, string? message, bool includeUntracked = false, CancellationToken ct = default);
    Task ApplyStashAsync(string repositoryPath, int index, CancellationToken ct = default);
    Task StashPopAsync(string repositoryPath, CancellationToken ct = default);
    Task DropStashAsync(string repositoryPath, int index, CancellationToken ct = default);
    Task<IReadOnlyList<(FilePath Path, ChangeKind Kind)>> GetStashFilesAsync(
        string repositoryPath, int index, CancellationToken ct = default);
    Task<string> GetStashPatchAsync(
        string repositoryPath,
        int index,
        FilePath path,
        DiffOptions options,
        CancellationToken ct = default);
}

public interface IGitHistoryService
{
    Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(
        string repositoryPath,
        int skip,
        int take,
        CancellationToken ct = default);

    /// <summary>
    /// Commits reachable from <paramref name="headRef"/> but not from <paramref name="baseRef"/>
    /// (<c>base..head</c>). Default order is newest-first; set <paramref name="oldestFirst"/> for
    /// interactive-rebase todo order.
    /// </summary>
    Task<IReadOnlyList<CommitInfo>> ListCommitsRangeAsync(
        string repositoryPath,
        string baseRef,
        string headRef = "HEAD",
        bool oldestFirst = false,
        CancellationToken ct = default);

    /// <summary>
    /// Recent commits that touched <paramref name="path"/> (follows renames). Newest first.
    /// </summary>
    Task<IReadOnlyList<CommitInfo>> ListFileHistoryAsync(
        string repositoryPath,
        string path,
        int take,
        CancellationToken ct = default);

    /// <summary>The commit that first added <paramref name="path"/> (follows renames), or null.</summary>
    Task<CommitInfo?> GetFileCreatedCommitAsync(
        string repositoryPath,
        string path,
        CancellationToken ct = default);

    Task<IReadOnlyList<(FilePath Path, ChangeKind Kind)>> GetCommitFilesAsync(
        string repositoryPath,
        string oid,
        CancellationToken ct = default);

    Task<CommitStat> GetCommitStatAsync(
        string repositoryPath,
        string oid,
        CancellationToken ct = default);

    Task<string> GetCommitPatchAsync(
        string repositoryPath,
        string oid,
        FilePath path,
        DiffOptions options,
        CancellationToken ct = default);
}

public interface IDiffCache
{
    bool TryGet(FileDiffKey key, out FileDiff? diff);
    void Set(FileDiffKey key, FileDiff diff);
    int HitCount { get; }
    int MissCount { get; }
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
    void Update(Action<AppSettings> mutate);
}

public interface IRepositoryGate
{
    Task<T> RunReadAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
    Task<T> RunIndexWriteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
    Task<T> RunWorktreeWriteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
    Task RunNetworkAsync(Func<CancellationToken, Task> action, CancellationToken ct);
    long CurrentEpoch { get; }
}

public interface IRepositoryGateProvider
{
    IRepositoryGate For(string repositoryPath);
}

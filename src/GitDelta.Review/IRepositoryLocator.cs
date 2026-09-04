namespace GitDelta.Review;

public sealed record LocatedRepository(
    string LocalPath,
    string? Host,
    string? Owner,
    string? Name,
    string? RemoteUrl,
    string? CurrentBranch = null,
    bool IsLinkedWorktree = false,
    string? MainWorktreePath = null);

public interface IRepositoryLocator
{
    /// <summary>Scans DevelopmentFolder and resolves remotes (used for PR → clone matching).</summary>
    IAsyncEnumerable<LocatedRepository> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Lightweight scan for the repository switcher: paths + current branch from HEAD, no remotes.
    /// </summary>
    IAsyncEnumerable<LocatedRepository> ScanLocalAsync(CancellationToken ct = default);

    /// <summary>
    /// Repository switcher catalog: DevelopmentFolder scan plus optional seeds (recent, pinned,
    /// current), merged with linked worktrees discovered via <c>git worktree list</c>.
    /// </summary>
    IAsyncEnumerable<LocatedRepository> ScanCatalogAsync(
        IEnumerable<string>? extraSeeds = null,
        CancellationToken ct = default);
}

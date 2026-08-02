namespace CodeReviewr.Review;

public sealed record LocatedRepository(
    string LocalPath,
    string? Host,
    string? Owner,
    string? Name,
    string? RemoteUrl,
    string? CurrentBranch = null);

public interface IRepositoryLocator
{
    /// <summary>Scans DevelopmentFolder and resolves remotes (used for PR → clone matching).</summary>
    IAsyncEnumerable<LocatedRepository> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Lightweight scan for the repository switcher: paths + current branch from HEAD, no remotes.
    /// </summary>
    IAsyncEnumerable<LocatedRepository> ScanLocalAsync(CancellationToken ct = default);
}

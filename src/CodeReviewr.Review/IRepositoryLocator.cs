namespace CodeReviewr.Review;

public sealed record LocatedRepository(
    string LocalPath,
    string? Host,
    string? Owner,
    string? Name,
    string? RemoteUrl);

public interface IRepositoryLocator
{
    IAsyncEnumerable<LocatedRepository> ScanAsync(CancellationToken ct = default);
}

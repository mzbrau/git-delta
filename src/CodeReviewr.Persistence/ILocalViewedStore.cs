namespace CodeReviewr.Persistence;

public interface ILocalViewedStore
{
    Task SetViewedAsync(
        string prNodeId,
        string path,
        string contentId,
        DateTimeOffset viewedUtc,
        CancellationToken ct = default);

    Task RemoveViewedAsync(string prNodeId, string path, CancellationToken ct = default);

    Task<IReadOnlyList<LocalViewedEntry>> ListAsync(string prNodeId, CancellationToken ct = default);

    Task<bool> IsViewedAsync(string prNodeId, string path, CancellationToken ct = default);
}

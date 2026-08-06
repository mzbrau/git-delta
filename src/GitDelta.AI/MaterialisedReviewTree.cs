using GitDelta.Core;
using GitDelta.Review;

namespace GitDelta.AI;

/// <summary>
/// Wraps an <see cref="IReviewTree"/> to expose a filesystem path where the same commit has been
/// materialised to disk (see <see cref="ReviewTreeMaterialiser"/>), reading from disk when
/// possible and falling back to the inner tree (e.g. `git show`) otherwise.
/// </summary>
internal sealed class MaterialisedReviewTree(IReviewTree inner, string materialisedPath) : IReviewTree
{
    public string? MaterialisedPath => materialisedPath;

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(FilePath path, CancellationToken ct)
    {
        var fullPath = ToAbsolutePath(path);
        if (File.Exists(fullPath))
            return await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);

        return await inner.ReadAsync(path, ct).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<FilePath>> ListAsync(FilePath prefix, CancellationToken ct) =>
        inner.ListAsync(prefix, ct);

    public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string pattern, CancellationToken ct) =>
        inner.SearchAsync(pattern, ct);

    private string ToAbsolutePath(FilePath path) =>
        Path.Combine(materialisedPath, path.Value.Replace('/', Path.DirectorySeparatorChar));
}

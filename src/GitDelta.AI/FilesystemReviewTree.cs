using GitDelta.Core;
using GitDelta.Review;

namespace GitDelta.AI;

/// <summary>Read-only <see cref="IReviewTree"/> backed by an already-materialised directory on disk.</summary>
internal sealed class FilesystemReviewTree(string materialisedPath) : IReviewTree
{
    public string? MaterialisedPath => materialisedPath;

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(FilePath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = ToFullPath(path);
        if (!File.Exists(fullPath))
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);

        var bytes = File.ReadAllBytes(fullPath);
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);
    }

    public ValueTask<IReadOnlyList<FilePath>> ListAsync(FilePath prefix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(materialisedPath))
            return ValueTask.FromResult<IReadOnlyList<FilePath>>([]);

        var prefixValue = prefix.Value.Trim('/');
        var results = new List<FilePath>();
        foreach (var file in Directory.EnumerateFiles(materialisedPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = System.IO.Path.GetRelativePath(materialisedPath, file)
                .Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (prefixValue.Length > 0 &&
                !relative.Equals(prefixValue, StringComparison.Ordinal) &&
                !relative.StartsWith(prefixValue + "/", StringComparison.Ordinal))
                continue;

            results.Add(FilePath.From(relative));
        }

        results.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return ValueTask.FromResult<IReadOnlyList<FilePath>>(results);
    }

    public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string pattern, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<SearchHit>>([]);

    private string ToFullPath(FilePath path)
    {
        var relative = path.Value.Replace('/', System.IO.Path.DirectorySeparatorChar);
        return System.IO.Path.Combine(materialisedPath, relative);
    }
}

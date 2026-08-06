using GitDelta.Core;

namespace GitDelta.Review;

public interface IReviewTree
{
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(FilePath path, CancellationToken ct);
    ValueTask<IReadOnlyList<FilePath>> ListAsync(FilePath prefix, CancellationToken ct);
    ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string pattern, CancellationToken ct);
    string? MaterialisedPath { get; }
}

public readonly record struct SearchHit(FilePath Path, int Line, string Text);

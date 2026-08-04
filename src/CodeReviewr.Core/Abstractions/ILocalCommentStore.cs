using CodeReviewr.Core.Diff;

namespace CodeReviewr.Core.Abstractions;

public interface ILocalCommentStore
{
    Task<LocalCommentRecord> AddAsync(LocalCommentCreate create, CancellationToken ct = default);
    Task<IReadOnlyList<LocalCommentRecord>> ListAsync(string repositoryKey, CancellationToken ct = default);
    Task<int> CountUnresolvedAsync(string repositoryKey, CancellationToken ct = default);
    Task SetResolvedAsync(string id, bool isResolved, CancellationToken ct = default);
    Task UpdateBodyAsync(string id, string body, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public sealed record LocalCommentCreate(
    string RepositoryKey,
    string Path,
    int StartLine,
    int EndLine,
    DiffSide Side,
    string Body,
    string? ContentId = null);

public sealed record LocalCommentRecord(
    string Id,
    string RepositoryKey,
    string Path,
    int StartLine,
    int EndLine,
    DiffSide Side,
    string Body,
    bool IsResolved,
    string? ContentId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

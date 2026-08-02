using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.Core;

/// <summary>Convenience overloads for working-copy diffs via <see cref="DiffScope.WorkingCopy"/>.</summary>
public static class GitDiffServiceExtensions
{
    public static Task<FileDiff> GetDiffAsync(
        this IGitDiffService service,
        string repositoryPath,
        FilePath path,
        DiffTarget target,
        DiffOptions options,
        CancellationToken ct = default) =>
        service.GetDiffAsync(repositoryPath, path, target.AsWorkingCopy(), options, ct);

    public static Task<IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)>> GetRawDiffAsync(
        this IGitDiffService service,
        string repositoryPath,
        DiffTarget target,
        DiffOptions options,
        CancellationToken ct = default) =>
        service.GetRawDiffAsync(repositoryPath, target.AsWorkingCopy(), options, ct);

    public static Task<FileDiff> GetWorkingCopyDiffAsync(
        this IGitDiffService service,
        string repositoryPath,
        FilePath path,
        DiffTarget target,
        DiffOptions options,
        CancellationToken ct = default) =>
        service.GetDiffAsync(repositoryPath, path, target.AsWorkingCopy(), options, ct);

    public static Task<IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)>> GetWorkingCopyRawDiffAsync(
        this IGitDiffService service,
        string repositoryPath,
        DiffTarget target,
        DiffOptions options,
        CancellationToken ct = default) =>
        service.GetRawDiffAsync(repositoryPath, target.AsWorkingCopy(), options, ct);
}

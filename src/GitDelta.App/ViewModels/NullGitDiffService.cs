using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;

namespace GitDelta.App.ViewModels;

/// <summary>Test/DI fallback that never loads diffs (browse requires a real <see cref="IGitDiffService"/>).</summary>
internal sealed class NullGitDiffService : IGitDiffService
{
    public Task<FileDiff> GetDiffAsync(
        string repositoryPath,
        FilePath path,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default) =>
        Task.FromException<FileDiff>(new InvalidOperationException("Diff service is not available."));

    public Task<IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)>> GetRawDiffAsync(
        string repositoryPath,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default) =>
        Task.FromException<IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)>>(
            new InvalidOperationException("Diff service is not available."));
}

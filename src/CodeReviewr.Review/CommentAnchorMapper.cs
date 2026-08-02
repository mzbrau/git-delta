using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diff;
using CodeReviewr.Git;

namespace CodeReviewr.Review;

public sealed class CommentAnchorMapper(
    IGitProcessRunner runner,
    IRepositoryGateProvider gates,
    IGitObjectReader objectReader)
{
    public async Task<ReviewThread> MapThreadAsync(
        ReviewSession session,
        ReviewThread thread,
        FilePath path,
        FileDiff fileDiff,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(thread.Path) ||
            thread.Side is null ||
            thread.Line is null)
        {
            return thread with { IsUnplaceable = true, Anchor = null };
        }

        var side = thread.Side.Value;
        var targetCommit = side == DiffSide.New
            ? session.Head.Value
            : session.MergeBase.Value;

        var sourceCommit = thread.OriginalCommitOid ?? thread.CommitOid ?? targetCommit;
        var sourceLine = thread.OriginalLine ?? thread.Line.Value;
        var sourceStartLine = thread.OriginalStartLine ?? thread.StartLine;

        var targetContentId = side == DiffSide.New ? fileDiff.NewContent : fileDiff.OldContent;
        if (targetContentId.IsEmpty)
        {
            return thread with { IsUnplaceable = true, Anchor = null };
        }

        var needsMigration = thread.IsOutdated ||
                             !string.Equals(thread.CommitOid, targetCommit, StringComparison.OrdinalIgnoreCase);

        if (!needsMigration)
        {
            var line = thread.Line.Value;
            var start = thread.StartLine ?? line;
            var end = line;
            if (start > end)
                (start, end) = (end, start);

            var startAnchor = new DiffAnchor(side, targetContentId, start);
            var endAnchor = new DiffAnchor(side, targetContentId, end);
            var anchor = start == end
                ? new AnnotationRange(startAnchor, startAnchor)
                : new AnnotationRange(startAnchor, endAnchor);

            return thread with { Anchor = anchor, IsUnplaceable = false };
        }

        ContentId sourceContentId;
        try
        {
            sourceContentId = await RevParseBlobAsync(session.RepositoryPath, sourceCommit, path, ct)
                .ConfigureAwait(false);
        }
        catch (GitException)
        {
            return thread with { IsUnplaceable = true, Anchor = null };
        }

        var sourceLines = await AnchorMigrator.ReadBlobLinesAsync(
                session.RepositoryPath, sourceContentId, objectReader, ct)
            .ConfigureAwait(false);
        var targetLines = await AnchorMigrator.ReadBlobLinesAsync(
                session.RepositoryPath, targetContentId, objectReader, ct)
            .ConfigureAwait(false);

        return AnchorMigrator.Migrate(
            thread,
            side,
            targetContentId,
            sourceLines,
            targetLines,
            sourceLine,
            sourceStartLine);
    }

    public async Task<IReadOnlyList<ReviewThread>> MapThreadsAsync(
        ReviewSession session,
        IReadOnlyList<ReviewThread> threads,
        FilePath path,
        FileDiff fileDiff,
        CancellationToken ct = default)
    {
        var mapped = new List<ReviewThread>(threads.Count);
        foreach (var thread in threads.Where(t =>
                     string.Equals(t.Path, path.Value, StringComparison.Ordinal)))
        {
            mapped.Add(await MapThreadAsync(session, thread, path, fileDiff, ct).ConfigureAwait(false));
        }

        return mapped;
    }

    private Task<ContentId> RevParseBlobAsync(
        string repositoryPath,
        string commitSha,
        FilePath path,
        CancellationToken ct) =>
        gates.For(repositoryPath).RunReadAsync(async token =>
        {
            var spec = $"{commitSha}:{path.Value}";
            var result = await runner.RunAsync(
                    repositoryPath,
                    ["rev-parse", spec],
                    options: null,
                    token)
                .ConfigureAwait(false);

            if (!result.Succeeded)
                throw new GitException($"git rev-parse {spec} failed: {result.Stderr}");

            var sha = result.Stdout.Trim();
            if (sha.Length == 0)
                throw new GitException($"git rev-parse {spec} returned empty output");

            return ContentId.FromSha(sha);
        }, ct);
}

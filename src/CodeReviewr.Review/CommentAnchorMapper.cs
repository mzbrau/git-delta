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
        if (string.IsNullOrEmpty(thread.Path))
            return thread with { IsUnplaceable = true, IsFileLevel = false, Anchor = null };

        // File-level review threads have no line/side; show them as file comments, not unplaceable.
        if (thread.SubjectType == ReviewThreadSubjectType.File ||
            (thread.Side is null && thread.Line is null && thread.OriginalLine is null))
        {
            return thread with
            {
                IsFileLevel = true,
                IsUnplaceable = false,
                Anchor = null,
                SubjectType = ReviewThreadSubjectType.File,
            };
        }

        // Incomplete payloads sometimes omit diffSide while still providing a line — assume RIGHT.
        var side = thread.Side ?? DiffSide.New;
        var targetCommit = side == DiffSide.New
            ? session.Head.Value
            : session.MergeBase.Value;

        var targetContentId = side == DiffSide.New ? fileDiff.NewContent : fileDiff.OldContent;
        if (targetContentId.IsEmpty)
            return thread with { IsUnplaceable = true, IsFileLevel = false, Anchor = null };

        // GitHub's current line (when present) is authoritative on the three-dot diff, even when
        // isOutdated is true. Comment commit OIDs are usually the head SHA and must not force
        // migration — especially for LEFT, where the line is in merge-base coordinates.
        if (thread.Line is { } currentLine)
            return PlaceOnTarget(thread, side, targetContentId, currentLine, thread.StartLine);

        var sourceLine = thread.OriginalLine;
        if (sourceLine is null)
            return thread with { IsUnplaceable = true, IsFileLevel = false, Anchor = null };

        // LEFT lines live on the merge-base blob; never use head CommitOid as the migration source.
        var sourceCommit = side == DiffSide.Old
            ? thread.OriginalCommitOid ?? targetCommit
            : thread.OriginalCommitOid ?? thread.CommitOid ?? targetCommit;
        var sourceStartLine = thread.OriginalStartLine ?? thread.StartLine;

        ContentId sourceContentId;
        try
        {
            sourceContentId = await RevParseBlobAsync(session.RepositoryPath, sourceCommit, path, ct)
                .ConfigureAwait(false);
        }
        catch (GitException)
        {
            return thread with { IsUnplaceable = true, IsFileLevel = false, Anchor = null };
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
            sourceLine.Value,
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

    private static ReviewThread PlaceOnTarget(
        ReviewThread thread,
        DiffSide side,
        ContentId targetContentId,
        int line,
        int? startLine)
    {
        var start = startLine ?? line;
        var end = line;
        if (start > end)
            (start, end) = (end, start);

        var startAnchor = new DiffAnchor(side, targetContentId, start);
        var endAnchor = new DiffAnchor(side, targetContentId, end);
        var anchor = start == end
            ? new AnnotationRange(startAnchor, startAnchor)
            : new AnnotationRange(startAnchor, endAnchor);

        return thread with
        {
            Side = side,
            Anchor = anchor,
            IsUnplaceable = false,
            IsFileLevel = false,
        };
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

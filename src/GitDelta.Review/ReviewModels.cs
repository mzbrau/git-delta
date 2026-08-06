using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.Review;

public sealed class HeadMovedException : Exception
{
    public HeadMovedException(string expectedSha, string actualSha)
        : base($"Pull request head moved from {expectedSha} to {actualSha}.")
    {
        ExpectedSha = expectedSha;
        ActualSha = actualSha;
    }

    public string ExpectedSha { get; }
    public string ActualSha { get; }
}

public enum SubmitReviewEvent
{
    Approve,
    Comment,
    RequestChanges,
}

public enum ReviewThreadSubjectType
{
    Line,
    File,
}

public sealed record ReviewComment(
    string NodeId,
    string Body,
    string? AuthorLogin,
    bool ViewerDidAuthor,
    DateTimeOffset CreatedAt,
    string? Url);

public sealed record ReviewThread(
    string NodeId,
    string Path,
    int? Line,
    int? StartLine,
    bool IsResolved,
    bool IsOutdated,
    IReadOnlyList<ReviewComment> Comments,
    DiffSide? Side = null,
    string? CommitOid = null,
    string? OriginalCommitOid = null,
    int? OriginalLine = null,
    int? OriginalStartLine = null,
    string? DiffHunk = null,
    AnnotationRange? Anchor = null,
    bool IsUnplaceable = false,
    string? ContextLines = null,
    ReviewThreadSubjectType SubjectType = ReviewThreadSubjectType.Line,
    bool IsFileLevel = false,
    bool IsPendingSync = false)
{
    /// <summary>Context shown for unplaceable threads: migrated snippet, else a short DiffHunk excerpt.</summary>
    public string? DisplayContext
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ContextLines))
                return ContextLines;
            return FormatDiffHunkSnippet(DiffHunk);
        }
    }

    private static string? FormatDiffHunkSnippet(string? diffHunk)
    {
        if (string.IsNullOrWhiteSpace(diffHunk))
            return null;

        var lines = diffHunk.Replace("\r\n", "\n").Split('\n');
        var take = Math.Min(lines.Length, 8);
        var start = Math.Max(0, lines.Length - take);
        return string.Join('\n', lines[start..(start + take)]);
    }
}

public sealed record AddCommentPayload(
    string ClientCommentId,
    string Path,
    int? Line,
    int? StartLine,
    string Side,
    string Body,
    string HeadSha);

public sealed record ReplyCommentPayload(
    string ClientCommentId,
    string ThreadId,
    string Body);

public sealed record EditCommentPayload(
    string CommentId,
    string Body);

public sealed record DeleteCommentPayload(string CommentId);

public sealed record ResolveThreadPayload(string ThreadId);

public sealed record UnresolveThreadPayload(string ThreadId);

public sealed record MarkFileViewedPayload(string Path);

public sealed record UnmarkFileViewedPayload(string Path);

public sealed record SubmitReviewPayload(
    string Event,
    string? Body,
    string ExpectedHeadSha);

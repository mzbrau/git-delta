using System.Text.Json;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.Review;

internal static class ReviewThreadParser
{
    public static IReadOnlyList<ReviewThread> Parse(JsonElement data)
    {
        if (!data.TryGetProperty("repository", out var repository) ||
            repository.ValueKind == JsonValueKind.Null ||
            !repository.TryGetProperty("pullRequest", out var pullRequest) ||
            pullRequest.ValueKind == JsonValueKind.Null ||
            !pullRequest.TryGetProperty("reviewThreads", out var reviewThreads) ||
            !reviewThreads.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ReviewThread>();
        foreach (var thread in nodes.EnumerateArray())
        {
            if (thread.ValueKind == JsonValueKind.Null)
                continue;

            results.Add(ParseThread(thread));
        }

        return results;
    }

    private static ReviewThread ParseThread(JsonElement thread)
    {
        var comments = new List<ReviewComment>();
        if (thread.TryGetProperty("comments", out var commentsProp) &&
            commentsProp.TryGetProperty("nodes", out var commentNodes) &&
            commentNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var comment in commentNodes.EnumerateArray())
            {
                if (comment.ValueKind == JsonValueKind.Null)
                    continue;

                comments.Add(new ReviewComment(
                    comment.GetProperty("id").GetString() ?? "",
                    comment.GetProperty("body").GetString() ?? "",
                    comment.TryGetProperty("author", out var author) &&
                    author.ValueKind == JsonValueKind.Object &&
                    author.TryGetProperty("login", out var login) &&
                    login.ValueKind == JsonValueKind.String
                        ? login.GetString()
                        : null,
                    comment.TryGetProperty("viewerDidAuthor", out var viewerDidAuthor) &&
                    viewerDidAuthor.ValueKind == JsonValueKind.True,
                    DateTimeOffset.Parse(comment.GetProperty("createdAt").GetString() ?? ""),
                    comment.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                        ? url.GetString()
                        : null));
            }
        }

        DiffSide? side = null;
        if (thread.TryGetProperty("diffSide", out var diffSide) &&
            diffSide.ValueKind == JsonValueKind.String)
        {
            side = diffSide.GetString() switch
            {
                "LEFT" => DiffSide.Old,
                "RIGHT" => DiffSide.New,
                _ => null,
            };
        }

        int? line = thread.TryGetProperty("line", out var lineEl) && lineEl.ValueKind == JsonValueKind.Number
            ? lineEl.GetInt32()
            : null;
        int? startLine = thread.TryGetProperty("startLine", out var startEl) && startEl.ValueKind == JsonValueKind.Number
            ? startEl.GetInt32()
            : null;

        return new ReviewThread(
            NodeId: thread.GetProperty("id").GetString() ?? "",
            Path: thread.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                ? path.GetString() ?? ""
                : "",
            Line: line,
            StartLine: startLine,
            IsResolved: thread.TryGetProperty("isResolved", out var resolved) &&
                        resolved.ValueKind == JsonValueKind.True,
            IsOutdated: thread.TryGetProperty("isOutdated", out var outdated) &&
                        outdated.ValueKind == JsonValueKind.True,
            Comments: comments,
            Side: side,
            CommitOid: thread.TryGetProperty("commit", out var commit) &&
                       commit.ValueKind == JsonValueKind.Object &&
                       commit.TryGetProperty("oid", out var commitOid) &&
                       commitOid.ValueKind == JsonValueKind.String
                ? commitOid.GetString()
                : null,
            OriginalCommitOid: thread.TryGetProperty("originalCommit", out var originalCommit) &&
                               originalCommit.ValueKind == JsonValueKind.Object &&
                               originalCommit.TryGetProperty("oid", out var originalCommitOid) &&
                               originalCommitOid.ValueKind == JsonValueKind.String
                ? originalCommitOid.GetString()
                : null,
            OriginalLine: thread.TryGetProperty("originalLine", out var originalLine) &&
                          originalLine.ValueKind == JsonValueKind.Number
                ? originalLine.GetInt32()
                : null,
            OriginalStartLine: thread.TryGetProperty("originalStartLine", out var originalStartLine) &&
                               originalStartLine.ValueKind == JsonValueKind.Number
                ? originalStartLine.GetInt32()
                : null,
            DiffHunk: thread.TryGetProperty("diffHunk", out var diffHunk) &&
                      diffHunk.ValueKind == JsonValueKind.String
                ? diffHunk.GetString()
                : null);
    }
}

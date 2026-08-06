using System.Text.Json;
using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.Review;

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

        var subjectType = ReviewThreadSubjectType.Line;
        if (thread.TryGetProperty("subjectType", out var subjectTypeEl) &&
            subjectTypeEl.ValueKind == JsonValueKind.String &&
            string.Equals(subjectTypeEl.GetString(), "FILE", StringComparison.OrdinalIgnoreCase))
        {
            subjectType = ReviewThreadSubjectType.File;
        }

        int? line = thread.TryGetProperty("line", out var lineEl) && lineEl.ValueKind == JsonValueKind.Number
            ? lineEl.GetInt32()
            : null;
        int? startLine = thread.TryGetProperty("startLine", out var startEl) && startEl.ValueKind == JsonValueKind.Number
            ? startEl.GetInt32()
            : null;

        string? diffHunk = null;
        string? commitOid = null;
        string? originalCommitOid = null;
        if (thread.TryGetProperty("comments", out var commentsForFields) &&
            commentsForFields.TryGetProperty("nodes", out var fieldNodes) &&
            fieldNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var comment in fieldNodes.EnumerateArray())
            {
                if (comment.ValueKind == JsonValueKind.Null)
                    continue;

                if (diffHunk is null &&
                    comment.TryGetProperty("diffHunk", out var hunkEl) &&
                    hunkEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(hunkEl.GetString()))
                {
                    diffHunk = hunkEl.GetString();
                }

                if (commitOid is null &&
                    TryReadCommitOid(comment, "commit", out var commentCommitOid))
                {
                    commitOid = commentCommitOid;
                }

                if (originalCommitOid is null &&
                    TryReadCommitOid(comment, "originalCommit", out var commentOriginalOid))
                {
                    originalCommitOid = commentOriginalOid;
                }

                if (diffHunk is not null && commitOid is not null && originalCommitOid is not null)
                    break;
            }
        }

        // Legacy fixtures may still place these fields on the thread node.
        if (diffHunk is null &&
            thread.TryGetProperty("diffHunk", out var threadHunk) &&
            threadHunk.ValueKind == JsonValueKind.String)
        {
            diffHunk = threadHunk.GetString();
        }

        if (commitOid is null)
            TryReadCommitOid(thread, "commit", out commitOid);
        if (originalCommitOid is null)
            TryReadCommitOid(thread, "originalCommit", out originalCommitOid);

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
            CommitOid: commitOid,
            OriginalCommitOid: originalCommitOid,
            OriginalLine: thread.TryGetProperty("originalLine", out var originalLine) &&
                          originalLine.ValueKind == JsonValueKind.Number
                ? originalLine.GetInt32()
                : null,
            OriginalStartLine: thread.TryGetProperty("originalStartLine", out var originalStartLine) &&
                               originalStartLine.ValueKind == JsonValueKind.Number
                ? originalStartLine.GetInt32()
                : null,
            DiffHunk: diffHunk,
            SubjectType: subjectType,
            IsFileLevel: subjectType == ReviewThreadSubjectType.File);
    }

    private static bool TryReadCommitOid(JsonElement parent, string propertyName, out string? oid)
    {
        oid = null;
        if (!parent.TryGetProperty(propertyName, out var commit) ||
            commit.ValueKind != JsonValueKind.Object ||
            !commit.TryGetProperty("oid", out var oidEl) ||
            oidEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        oid = oidEl.GetString();
        return !string.IsNullOrEmpty(oid);
    }
}

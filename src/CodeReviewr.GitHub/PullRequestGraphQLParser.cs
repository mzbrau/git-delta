using System.Text.Json;

namespace CodeReviewr.GitHub;

internal static class PullRequestGraphQLParser
{
    public static PullRequestSummary ParseSummary(
        JsonElement pr,
        string host,
        string accountLogin,
        InboxSection section)
    {
        var repository = pr.GetProperty("repository");
        var owner = repository.GetProperty("owner").GetProperty("login").GetString()
            ?? throw new InvalidOperationException("Repository owner login missing.");
        var name = repository.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Repository name missing.");
        var nameWithOwner = repository.GetProperty("nameWithOwner").GetString() ?? $"{owner}/{name}";

        return new PullRequestSummary(
            NodeId: pr.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Pull request id missing."),
            Host: host,
            AccountLogin: accountLogin,
            RepositoryNodeId: repository.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Repository id missing."),
            Owner: owner,
            Name: name,
            NameWithOwner: nameWithOwner,
            Number: pr.GetProperty("number").GetInt32(),
            Title: pr.GetProperty("title").GetString() ?? string.Empty,
            Url: pr.GetProperty("url").GetString() ?? string.Empty,
            IsDraft: pr.GetProperty("isDraft").GetBoolean(),
            UpdatedAt: DateTimeOffset.Parse(pr.GetProperty("updatedAt").GetString()
                ?? throw new InvalidOperationException("Pull request updatedAt missing.")),
            ReviewDecision: pr.TryGetProperty("reviewDecision", out var rd) && rd.ValueKind != JsonValueKind.Null
                ? rd.GetString()
                : null,
            BaseRefName: pr.GetProperty("baseRefName").GetString() ?? string.Empty,
            HeadRefName: pr.GetProperty("headRefName").GetString() ?? string.Empty,
            BaseOid: pr.TryGetProperty("baseRefOid", out var baseOid) && baseOid.ValueKind == JsonValueKind.String
                ? baseOid.GetString()
                : null,
            HeadOid: ResolveHeadOid(pr),
            AuthorLogin: pr.TryGetProperty("author", out var author) &&
                         author.ValueKind == JsonValueKind.Object &&
                         author.TryGetProperty("login", out var login)
                ? login.GetString()
                : null,
            ChangedFiles: pr.GetProperty("changedFiles").GetInt32(),
            Section: section);
    }

    public static PullRequestDetail ParseDetail(
        JsonElement pr,
        string host,
        string accountLogin,
        InboxSection section)
    {
        var summary = ParseSummary(pr, host, accountLogin, section);
        var body = pr.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
            ? bodyProp.GetString()
            : null;

        var files = new List<PullRequestChangedFile>();
        if (pr.TryGetProperty("files", out var filesProp) &&
            filesProp.TryGetProperty("nodes", out var fileNodes) &&
            fileNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in fileNodes.EnumerateArray())
            {
                files.Add(new PullRequestChangedFile(
                    Path: file.GetProperty("path").GetString() ?? string.Empty,
                    ChangeType: file.GetProperty("changeType").GetString() ?? string.Empty,
                    Additions: file.GetProperty("additions").GetInt32(),
                    Deletions: file.GetProperty("deletions").GetInt32()));
            }
        }

        string? checkRollupState = null;
        var statusChecks = new List<StatusCheckItem>();
        if (pr.TryGetProperty("commits", out var commits) &&
            commits.TryGetProperty("nodes", out var commitNodes) &&
            commitNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var commitNode in commitNodes.EnumerateArray())
            {
                if (!commitNode.TryGetProperty("commit", out var commit))
                    continue;

                if (commit.TryGetProperty("statusCheckRollup", out var rollup) &&
                    rollup.ValueKind == JsonValueKind.Object)
                {
                    if (rollup.TryGetProperty("state", out var state) &&
                        state.ValueKind == JsonValueKind.String)
                    {
                        checkRollupState = state.GetString();
                    }

                    if (rollup.TryGetProperty("contexts", out var contexts) &&
                        contexts.TryGetProperty("nodes", out var contextNodes) &&
                        contextNodes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var context in contextNodes.EnumerateArray())
                        {
                            if (context.TryGetProperty("name", out var checkName) &&
                                checkName.ValueKind == JsonValueKind.String)
                            {
                                context.TryGetProperty("detailsUrl", out var detailsUrl);
                                context.TryGetProperty("conclusion", out var conclusion);
                                statusChecks.Add(new StatusCheckItem(
                                    checkName.GetString() ?? string.Empty,
                                    detailsUrl.ValueKind == JsonValueKind.String ? detailsUrl.GetString() : null,
                                    conclusion.ValueKind == JsonValueKind.String
                                        ? conclusion.GetString() ?? string.Empty
                                        : string.Empty));
                                continue;
                            }

                            if (context.TryGetProperty("context", out var statusContext) &&
                                statusContext.ValueKind == JsonValueKind.String)
                            {
                                context.TryGetProperty("targetUrl", out var targetUrl);
                                context.TryGetProperty("state", out var statusState);
                                statusChecks.Add(new StatusCheckItem(
                                    statusContext.GetString() ?? string.Empty,
                                    targetUrl.ValueKind == JsonValueKind.String ? targetUrl.GetString() : null,
                                    statusState.ValueKind == JsonValueKind.String
                                        ? statusState.GetString() ?? string.Empty
                                        : string.Empty));
                            }
                        }
                    }
                }

                if (checkRollupState is not null)
                    break;
            }
        }

        bool? mergeable = null;
        if (pr.TryGetProperty("mergeable", out var mergeableProp) &&
            mergeableProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            mergeable = mergeableProp.GetBoolean();
        }

        string? mergeStateStatus = null;
        if (pr.TryGetProperty("mergeStateStatus", out var mergeState) &&
            mergeState.ValueKind == JsonValueKind.String)
        {
            mergeStateStatus = mergeState.GetString();
        }

        var timeline = ParseTimeline(pr);

        return new PullRequestDetail(
            summary,
            body,
            files,
            checkRollupState,
            mergeable,
            mergeStateStatus,
            statusChecks,
            timeline);
    }

    private static IReadOnlyList<PullRequestTimelineEntry> ParseTimeline(JsonElement pr)
    {
        var timeline = new List<PullRequestTimelineEntry>();

        if (pr.TryGetProperty("comments", out var comments) &&
            comments.TryGetProperty("nodes", out var commentNodes) &&
            commentNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var comment in commentNodes.EnumerateArray())
            {
                var body = comment.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
                    ? bodyProp.GetString() ?? string.Empty
                    : string.Empty;
                var createdAt = comment.TryGetProperty("createdAt", out var createdProp) &&
                                createdProp.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(createdProp.GetString()!)
                    : DateTimeOffset.MinValue;
                string? author = null;
                if (comment.TryGetProperty("author", out var authorProp) &&
                    authorProp.ValueKind == JsonValueKind.Object &&
                    authorProp.TryGetProperty("login", out var login) &&
                    login.ValueKind == JsonValueKind.String)
                {
                    author = login.GetString();
                }

                timeline.Add(new PullRequestTimelineEntry(
                    Kind: "comment",
                    AuthorLogin: author,
                    Body: body,
                    CreatedAt: createdAt,
                    Url: comment.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                        ? url.GetString()
                        : null,
                    ReviewState: null));
            }
        }

        if (pr.TryGetProperty("reviews", out var reviews) &&
            reviews.TryGetProperty("nodes", out var reviewNodes) &&
            reviewNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var review in reviewNodes.EnumerateArray())
            {
                var body = review.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
                    ? bodyProp.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(body) &&
                    review.TryGetProperty("state", out var stateOnly) &&
                    stateOnly.ValueKind == JsonValueKind.String)
                {
                    body = stateOnly.GetString() ?? string.Empty;
                }

                var submittedAt = review.TryGetProperty("submittedAt", out var submittedProp) &&
                                  submittedProp.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(submittedProp.GetString()!)
                    : DateTimeOffset.MinValue;
                string? author = null;
                if (review.TryGetProperty("author", out var authorProp) &&
                    authorProp.ValueKind == JsonValueKind.Object &&
                    authorProp.TryGetProperty("login", out var login) &&
                    login.ValueKind == JsonValueKind.String)
                {
                    author = login.GetString();
                }

                timeline.Add(new PullRequestTimelineEntry(
                    Kind: "review",
                    AuthorLogin: author,
                    Body: body,
                    CreatedAt: submittedAt,
                    Url: review.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                        ? url.GetString()
                        : null,
                    ReviewState: review.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                        ? state.GetString()
                        : null));
            }
        }

        return timeline
            .OrderByDescending(t => t.CreatedAt)
            .Take(30)
            .ToList();
    }

    public static IReadOnlyList<PullRequestSummary> ParseInboxSearch(
        JsonElement data,
        string host,
        string accountLogin,
        InboxSection section)
    {
        var results = new List<PullRequestSummary>();
        if (!data.TryGetProperty("search", out var search) ||
            !search.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
                continue;

            results.Add(ParseSummary(node, host, accountLogin, section));
        }

        return results;
    }

    private static string? ResolveHeadOid(JsonElement pr)
    {
        if (pr.TryGetProperty("headRefOid", out var headRefOid) && headRefOid.ValueKind == JsonValueKind.String)
            return headRefOid.GetString();

        if (pr.TryGetProperty("commits", out var commits) &&
            commits.TryGetProperty("nodes", out var nodes) &&
            nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.TryGetProperty("commit", out var commit) &&
                    commit.TryGetProperty("oid", out var oid) &&
                    oid.ValueKind == JsonValueKind.String)
                {
                    return oid.GetString();
                }
            }
        }

        return null;
    }
}

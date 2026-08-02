namespace CodeReviewr.GitHub;

public enum InboxSection
{
    NeedsMyReview,
    Reviewed,
    MyPullRequests,
}

public sealed record PullRequestSummary(
    string NodeId,
    string Host,
    string AccountLogin,
    string RepositoryNodeId,
    string Owner,
    string Name,
    string NameWithOwner,
    int Number,
    string Title,
    string Url,
    bool IsDraft,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ReviewDecision,
    string BaseRefName,
    string HeadRefName,
    string? BaseOid,
    string? HeadOid,
    string? AuthorLogin,
    int ChangedFiles,
    InboxSection Section);

public sealed record PullRequestDetail(
    PullRequestSummary Summary,
    string? Body,
    IReadOnlyList<PullRequestChangedFile> Files,
    string? CheckRollupState,
    bool? Mergeable = null,
    string? MergeStateStatus = null,
    IReadOnlyList<StatusCheckItem>? StatusChecks = null,
    IReadOnlyList<PullRequestTimelineEntry>? Timeline = null,
    IReadOnlyList<PullRequestReviewerStatus>? Reviewers = null,
    string? ViewerReviewState = null);

public sealed record PullRequestChangedFile(string Path, string ChangeType, int Additions, int Deletions);

public sealed record StatusCheckItem(string Name, string? Url, string State);

public sealed record PullRequestTimelineEntry(
    string Kind,
    string? AuthorLogin,
    string Body,
    DateTimeOffset CreatedAt,
    string? Url,
    string? ReviewState);

/// <summary>Other reviewers for a PR (latest review state or outstanding review request).</summary>
public sealed record PullRequestReviewerStatus(
    string Login,
    string? AvatarUrl,
    string State);

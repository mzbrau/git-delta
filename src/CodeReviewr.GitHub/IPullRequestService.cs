namespace CodeReviewr.GitHub;

public interface IPullRequestService
{
    Task<IReadOnlyList<PullRequestSummary>> GetInboxAsync(CancellationToken ct = default);
    Task<PullRequestDetail> GetPullRequestAsync(
        string host,
        string accountLogin,
        string owner,
        string name,
        int number,
        CancellationToken ct = default);

    /// <summary>Total comments on the viewer's PENDING review for this PR (0 if none).</summary>
    Task<int> GetPendingReviewCommentCountAsync(
        string host,
        string accountLogin,
        string owner,
        string name,
        int number,
        CancellationToken ct = default);
}

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
}

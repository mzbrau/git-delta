using CodeReviewr.Core;

namespace CodeReviewr.Review;

public interface IPullRequestGitService
{
    Task FetchPullRequestHeadAsync(
        string repoPath,
        string remoteOrUrl,
        int prNumber,
        CancellationToken ct = default);

    Task<CommitId> ResolveMergeBaseAsync(
        string repoPath,
        CommitId baseOid,
        CommitId headOid,
        CancellationToken ct = default);
}

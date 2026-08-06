using GitDelta.Core;

namespace GitDelta.Review;

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

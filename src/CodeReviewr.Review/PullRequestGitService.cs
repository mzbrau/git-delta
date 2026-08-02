using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Git;

namespace CodeReviewr.Review;

public sealed class PullRequestGitService(
    IGitProcessRunner runner,
    IRepositoryGateProvider gates) : IPullRequestGitService
{
    public static string LocalRefName(int prNumber) => $"refs/codereviewr/pr/{prNumber}";

    public Task FetchPullRequestHeadAsync(
        string repoPath,
        string remoteOrUrl,
        int prNumber,
        CancellationToken ct = default)
    {
        var localRef = LocalRefName(prNumber);
        var fetchSpec = $"refs/pull/{prNumber}/head:{localRef}";

        return gates.For(repoPath).RunNetworkAsync(async token =>
        {
            var result = await runner.RunAsync(
                    repoPath,
                    ["fetch", "--force", remoteOrUrl, fetchSpec],
                    options: null,
                    token)
                .ConfigureAwait(false);

            if (!result.Succeeded)
                throw new GitException($"git fetch pull request head failed: {result.Stderr}");
        }, ct);
    }

    public Task<CommitId> ResolveMergeBaseAsync(
        string repoPath,
        CommitId baseOid,
        CommitId headOid,
        CancellationToken ct = default) =>
        gates.For(repoPath).RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                    repoPath,
                    ["merge-base", baseOid.Value, headOid.Value],
                    options: null,
                    token)
                .ConfigureAwait(false);

            if (!result.Succeeded)
                throw new GitException($"git merge-base failed: {result.Stderr}");

            var sha = result.Stdout.Trim();
            if (sha.Length == 0)
                throw new GitException("git merge-base returned empty output");

            return CommitId.FromSha(sha);
        }, ct);
}

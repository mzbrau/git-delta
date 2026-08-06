using GitDelta.Core;
using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace GitDelta.Review.Tests;

public sealed class PullRequestGitServiceTests
{
    private static PullRequestGitService CreateService()
    {
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance, commandLog: null, assertNoUiSyncContext: false);
        var gates = new RepositoryGateProvider(runner);
        return new PullRequestGitService(runner, gates);
    }

    [Test]
    public async Task FetchPullRequestHead_And_ResolveMergeBase()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "base\n")
            .WithInitialCommit("root")
            .WithFile("a.txt", "feature\n")
            .WithCommit("pr-head");
        var path = repo.Build();

        var baseOid = repo.RunGit("rev-parse", "HEAD~1").Trim();
        var headOid = repo.RunGit("rev-parse", "HEAD").Trim();
        repo.RunGit("update-ref", "refs/pull/42/head", headOid);

        var service = CreateService();
        await service.FetchPullRequestHeadAsync(path, path, prNumber: 42);

        var localRef = PullRequestGitService.LocalRefName(42);
        var fetched = repo.RunGit("rev-parse", localRef).Trim();
        Assert.That(fetched, Is.EqualTo(headOid));

        var mergeBase = await service.ResolveMergeBaseAsync(
            path,
            CommitId.FromSha(baseOid),
            CommitId.FromSha(headOid));
        Assert.That(mergeBase.Value, Is.EqualTo(baseOid));
    }
}

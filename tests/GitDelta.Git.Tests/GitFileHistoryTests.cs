using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class GitFileHistoryTests
{
    [Test]
    public async Task ListFileHistoryAsync_FollowsPath_AndReturnsNewestFirst()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("src/a.txt", "one\n")
            .WithInitialCommit("create a")
            .WithFile("src/a.txt", "one\ntwo\n")
            .WithCommit("update a");
        var path = repo.Build();

        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance, commandLog: null, assertNoUiSyncContext: false);
        var gates = new RepositoryGateProvider(runner);
        var history = new GitHistoryService(runner, gates);

        var commits = await history.ListFileHistoryAsync(path, "src/a.txt", take: 10);
        Assert.That(commits, Has.Count.EqualTo(2));
        Assert.That(commits[0].Subject, Is.EqualTo("update a"));
        Assert.That(commits[1].Subject, Is.EqualTo("create a"));

        var created = await history.GetFileCreatedCommitAsync(path, "src/a.txt");
        Assert.That(created, Is.Not.Null);
        Assert.That(created!.Subject, Is.EqualTo("create a"));
    }

    [Test]
    public async Task ListTrackedFilesAsync_ReturnsSortedIndexPaths()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("z.txt", "z\n")
            .WithFile("a/b.txt", "ab\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance, commandLog: null, assertNoUiSyncContext: false);
        var gates = new RepositoryGateProvider(runner);
        var history = new GitHistoryService(runner, gates);

        var files = await history.ListTrackedFilesAsync(path);
        Assert.That(files.Select(f => f.Value), Is.EqualTo(new[] { "a/b.txt", "z.txt" }));
    }
}

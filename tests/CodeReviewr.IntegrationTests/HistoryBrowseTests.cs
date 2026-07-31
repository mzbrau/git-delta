using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Caching;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CodeReviewr.IntegrationTests;

public sealed class HistoryBrowseTests
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDiffCache, MemoryDiffCache>();
        services.AddCodeReviewrGit();
        services.AddCodeReviewrDiff();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task List_Files_And_Patch_For_Commit()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("root commit")
            .WithFile("a.txt", "two\n")
            .WithFile("b.txt", "new\n")
            .WithCommit("second commit");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var commits = await history.ListCommitsAsync(path, skip: 0, take: 10);
        Assert.That(commits, Has.Count.EqualTo(2));
        Assert.That(commits[0].Subject, Is.EqualTo("second commit"));
        Assert.That(commits[1].Subject, Is.EqualTo("root commit"));
        Assert.That(commits[0].AuthorName, Is.EqualTo("Test"));
        Assert.That(commits[0].ParentOids, Has.Count.EqualTo(1));
        Assert.That(commits[1].IsRoot, Is.True);

        var files = await history.GetCommitFilesAsync(path, commits[0].Oid);
        Assert.That(files.Any(f => f.Path.Value == "a.txt"), Is.True);
        Assert.That(files.Any(f => f.Path.Value == "b.txt"), Is.True);

        var patch = await history.GetCommitPatchAsync(
            path, commits[0].Oid, FilePath.From("a.txt"), DiffOptions.Default);
        Assert.That(patch, Does.Contain("-one").Or.Contain("+two").Or.Contain("two"));
    }

    [Test]
    public async Task Root_Commit_Files_And_Patch_Do_Not_Throw()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("root.txt", "hello\n")
            .WithInitialCommit("initial");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var commits = await history.ListCommitsAsync(path, skip: 0, take: 5);
        Assert.That(commits, Has.Count.EqualTo(1));
        Assert.That(commits[0].IsRoot, Is.True);

        var files = await history.GetCommitFilesAsync(path, commits[0].Oid);
        Assert.That(files.Any(f => f.Path.Value == "root.txt"), Is.True);

        var patch = await history.GetCommitPatchAsync(
            path, commits[0].Oid, FilePath.From("root.txt"), DiffOptions.Default);
        Assert.That(patch, Does.Contain("hello").Or.Contain("root.txt"));
    }

    [Test]
    public async Task ListCommits_Respects_Skip_And_Take()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "1\n")
            .WithInitialCommit("c1")
            .WithFile("a.txt", "2\n")
            .WithCommit("c2")
            .WithFile("a.txt", "3\n")
            .WithCommit("c3");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var page = await history.ListCommitsAsync(path, skip: 1, take: 1);
        Assert.That(page, Has.Count.EqualTo(1));
        Assert.That(page[0].Subject, Is.EqualTo("c2"));
    }

    [Test]
    public void ParseCommitLog_Handles_Multiline_Body()
    {
        // Use \u001f / concatenation so \x escapes cannot eat following hex digits (e.g. year "2026").
        const char rs = '\u001e';
        const char us = '\u001f';
        var stdout =
            $"{rs}abc123{us}abc1234{us}parent1{us}Ann{us}ann@ex.com{us}2026-07-31T12:00:00+00:00{us}" +
            $"Subject line{us}Body line 1\nBody line 2{us}HEAD -> main, tag: v1";

        var commits = GitHistoryService.ParseCommitLog(stdout);
        Assert.That(commits, Has.Count.EqualTo(1));
        Assert.That(commits[0].Subject, Is.EqualTo("Subject line"));
        Assert.That(commits[0].Body, Does.Contain("Body line 1"));
        Assert.That(commits[0].Body, Does.Contain("Body line 2"));
        Assert.That(commits[0].Decorations, Does.Contain("main"));
        Assert.That(commits[0].Decorations, Does.Contain("v1"));
    }
}

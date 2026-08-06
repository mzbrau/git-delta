using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Caching;
using GitDelta.Diff;
using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace GitDelta.IntegrationTests;

public sealed class InteractiveRebaseTests
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDiffCache, MemoryDiffCache>();
        services.AddGitDeltaGit();
        services.AddGitDeltaDiff();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task ListCommitsRange_Returns_Commits_Ahead_Of_Base()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "1\n")
            .WithInitialCommit("root")
            .WithFile("a.txt", "2\n")
            .WithCommit("on-main");
        var path = repo.Build();
        repo.RunGit("checkout", "-b", "feature");
        // WithFile after Build doesn't auto-run — write directly.
        File.WriteAllText(Path.Combine(path, "a.txt"), "3\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "feat-1");
        File.WriteAllText(Path.Combine(path, "a.txt"), "4\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "feat-2");

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var newestFirst = await history.ListCommitsRangeAsync(path, "main", "HEAD");
        Assert.That(newestFirst.Select(c => c.Subject), Is.EqualTo(new[] { "feat-2", "feat-1" }));

        var oldestFirst = await history.ListCommitsRangeAsync(path, "main", "HEAD", oldestFirst: true);
        Assert.That(oldestFirst.Select(c => c.Subject), Is.EqualTo(new[] { "feat-1", "feat-2" }));
    }

    [Test]
    public async Task Interactive_Rebase_Squash_And_Reword()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "1\n")
            .WithInitialCommit("root");
        var path = repo.Build();
        repo.RunGit("checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(path, "a.txt"), "2\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "feat-1");
        File.WriteAllText(Path.Combine(path, "a.txt"), "3\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "feat-2");
        File.WriteAllText(Path.Combine(path, "a.txt"), "4\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "feat-3");

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var history = sp.GetRequiredService<IGitHistoryService>();
        var rebase = sp.GetRequiredService<IGitRebaseService>();

        var commits = await history.ListCommitsRangeAsync(path, "main", "HEAD", oldestFirst: true);
        Assert.That(commits, Has.Count.EqualTo(3));

        var todo = new[]
        {
            new RebaseTodoEntry(commits[0].Oid, RebaseTodoAction.Reword, "feature start"),
            new RebaseTodoEntry(commits[1].Oid, RebaseTodoAction.Squash, "feature squashed"),
            new RebaseTodoEntry(commits[2].Oid, RebaseTodoAction.Fixup),
        };

        var result = await rebase.StartInteractiveAsync(path, "main", todo);
        Assert.That(result.Outcome, Is.EqualTo(RebaseRunOutcome.Completed), result.Detail);

        var after = await history.ListCommitsRangeAsync(path, "main", "HEAD", oldestFirst: true);
        Assert.That(after, Has.Count.EqualTo(1));
        Assert.That(after[0].Subject, Is.EqualTo("feature squashed"));
    }

    [Test]
    public async Task Interactive_Rebase_Drop_Commit()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "1\n")
            .WithInitialCommit("root");
        var path = repo.Build();
        repo.RunGit("checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(path, "a.txt"), "2\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "keep");
        File.WriteAllText(Path.Combine(path, "b.txt"), "drop-me\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "drop-me");

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var history = sp.GetRequiredService<IGitHistoryService>();
        var rebase = sp.GetRequiredService<IGitRebaseService>();

        var commits = await history.ListCommitsRangeAsync(path, "main", "HEAD", oldestFirst: true);
        var todo = new[]
        {
            new RebaseTodoEntry(commits[0].Oid, RebaseTodoAction.Pick),
            new RebaseTodoEntry(commits[1].Oid, RebaseTodoAction.Drop),
        };

        var result = await rebase.StartInteractiveAsync(path, "main", todo);
        Assert.That(result.Outcome, Is.EqualTo(RebaseRunOutcome.Completed), result.Detail);

        var after = await history.ListCommitsRangeAsync(path, "main", "HEAD", oldestFirst: true);
        Assert.That(after, Has.Count.EqualTo(1));
        Assert.That(after[0].Subject, Is.EqualTo("keep"));
        Assert.That(File.Exists(Path.Combine(path, "b.txt")), Is.False);
    }

    [Test]
    public async Task Interactive_Rebase_Conflict_Then_Abort()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "base\n")
            .WithInitialCommit("root");
        var path = repo.Build();

        repo.RunGit("checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(path, "a.txt"), "feature\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "feature-change");

        repo.RunGit("checkout", "main");
        File.WriteAllText(Path.Combine(path, "a.txt"), "main\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "main-change");

        repo.RunGit("checkout", "feature");

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var history = sp.GetRequiredService<IGitHistoryService>();
        var rebase = sp.GetRequiredService<IGitRebaseService>();
        var conflicts = sp.GetRequiredService<IGitConflictService>();

        var commits = await history.ListCommitsRangeAsync(path, "main", "HEAD", oldestFirst: true);
        Assert.That(commits, Has.Count.EqualTo(1));

        var result = await rebase.StartInteractiveAsync(
            path,
            "main",
            [new RebaseTodoEntry(commits[0].Oid, RebaseTodoAction.Pick)]);
        Assert.That(result.Outcome, Is.EqualTo(RebaseRunOutcome.Conflicts));
        Assert.That(await conflicts.DetectInProgressAsync(path), Is.EqualTo(InProgressOperation.Rebase));

        await rebase.AbortAsync(path);
        Assert.That(await conflicts.DetectInProgressAsync(path), Is.EqualTo(InProgressOperation.None));
    }

    [Test]
    public async Task Interactive_Rebase_Conflict_Then_Continue_Applies_Later_Reword()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "base\n")
            .WithFile("b.txt", "b0\n")
            .WithInitialCommit("root");
        var path = repo.Build();

        repo.RunGit("checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(path, "a.txt"), "feature\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "feature-change");

        File.WriteAllText(Path.Combine(path, "b.txt"), "b1\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "old-subject");

        repo.RunGit("checkout", "main");
        File.WriteAllText(Path.Combine(path, "a.txt"), "main\n");
        repo.RunGit("add", "-A");
        repo.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "main-change");

        repo.RunGit("checkout", "feature");

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var history = sp.GetRequiredService<IGitHistoryService>();
        var rebase = sp.GetRequiredService<IGitRebaseService>();
        var staging = sp.GetRequiredService<IGitStagingService>();
        var conflicts = sp.GetRequiredService<IGitConflictService>();

        var commits = await history.ListCommitsRangeAsync(path, "main", "HEAD", oldestFirst: true);
        Assert.That(commits, Has.Count.EqualTo(2));

        var result = await rebase.StartInteractiveAsync(
            path,
            "main",
            [
                new RebaseTodoEntry(commits[0].Oid, RebaseTodoAction.Pick),
                new RebaseTodoEntry(commits[1].Oid, RebaseTodoAction.Reword, "rewritten-subject"),
            ]);
        Assert.That(result.Outcome, Is.EqualTo(RebaseRunOutcome.Conflicts));
        Assert.That(await conflicts.DetectInProgressAsync(path), Is.EqualTo(InProgressOperation.Rebase));

        File.WriteAllText(Path.Combine(path, "a.txt"), "resolved\n");
        await staging.StageFileAsync(path, FilePath.From("a.txt"));

        var continued = await rebase.ContinueAsync(path);
        Assert.That(continued.Outcome, Is.EqualTo(RebaseRunOutcome.Completed), continued.Detail);
        Assert.That(await conflicts.DetectInProgressAsync(path), Is.EqualTo(InProgressOperation.None));

        var after = await history.ListCommitsRangeAsync(path, "main", "HEAD", oldestFirst: true);
        Assert.That(after.Select(c => c.Subject), Is.EqualTo(new[] { "feature-change", "rewritten-subject" }));
        Assert.That(File.ReadAllText(Path.Combine(path, "a.txt")), Is.EqualTo("resolved\n"));
        Assert.That(File.ReadAllText(Path.Combine(path, "b.txt")), Is.EqualTo("b1\n"));
    }

    [Test]
    public async Task GetCommitStat_Returns_Counts()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("root")
            .WithFile("a.txt", "one\ntwo\n")
            .WithFile("b.txt", "new\n")
            .WithCommit("second");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var history = sp.GetRequiredService<IGitHistoryService>();
        var commits = await history.ListCommitsAsync(path, 0, 5);
        var tip = commits[0];
        var stat = await history.GetCommitStatAsync(path, tip.Oid);
        Assert.That(stat.FileCount, Is.EqualTo(2));
        Assert.That(stat.Insertions, Is.GreaterThanOrEqualTo(2));
    }
}

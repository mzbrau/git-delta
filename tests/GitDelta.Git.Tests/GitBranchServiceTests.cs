using GitDelta.Core;
using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class GitBranchServiceTests
{
    private static (GitBranchService Service, GitProcessRunner Runner) CreateService()
    {
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance, commandLog: null, assertNoUiSyncContext: false);
        var gates = new RepositoryGateProvider(runner);
        return (new GitBranchService(runner, gates), runner);
    }

    [Test]
    public async Task Create_Checkout_Rename_And_Delete_Branch()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var (service, _) = CreateService();

        await service.CreateBranchAsync(path, "feature", checkout: false);
        var afterCreate = await service.ListBranchesAsync(path);
        Assert.That(afterCreate.Any(b => b.Name == "feature" && !b.IsRemote), Is.True);
        Assert.That(afterCreate.Single(b => b.Name == "main" && !b.IsRemote).IsCurrent, Is.True);

        await service.CheckoutAsync(path, "feature");
        var afterCheckout = await service.ListBranchesAsync(path);
        Assert.That(afterCheckout.Single(b => b.Name == "feature" && !b.IsRemote).IsCurrent, Is.True);

        await service.RenameBranchAsync(path, "feature", "topic");
        var afterRename = await service.ListBranchesAsync(path);
        Assert.That(afterRename.Any(b => b.Name == "feature" && !b.IsRemote), Is.False);
        Assert.That(afterRename.Single(b => b.Name == "topic" && !b.IsRemote).IsCurrent, Is.True);

        await service.CreateBranchAsync(path, "scratch", checkout: true);
        await service.DeleteBranchAsync(path, "topic", force: false);

        var afterDelete = await service.ListBranchesAsync(path);
        Assert.That(afterDelete.Any(b => b.Name == "topic" && !b.IsRemote), Is.False);
        Assert.That(afterDelete.Single(b => b.Name == "scratch" && !b.IsRemote).IsCurrent, Is.True);
    }

    [Test]
    public async Task CreateBranch_WithCheckout_SwitchesImmediately()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var (service, _) = CreateService();
        await service.CreateBranchAsync(path, "hotfix", checkout: true);

        var branches = await service.ListBranchesAsync(path);
        Assert.That(branches.Single(b => b.Name == "hotfix" && !b.IsRemote).IsCurrent, Is.True);
        Assert.That(branches.Single(b => b.Name == "main" && !b.IsRemote).IsCurrent, Is.False);
    }

    [Test]
    public async Task GetDivergence_Reports_Ahead_And_Behind()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var (service, runner) = CreateService();

        await service.CreateBranchAsync(path, "feature", checkout: true);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "feature\n");
        await RunCommitAsync(runner, path, "feature commit");

        await service.CheckoutAsync(path, "main");
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "main\n");
        await RunCommitAsync(runner, path, "main commit");

        // From main's perspective of feature: feature is 1 ahead and 1 behind.
        var divergence = await service.GetDivergenceAsync(path, "main", "feature");
        Assert.That(divergence.Ahead, Is.EqualTo(1));
        Assert.That(divergence.Behind, Is.EqualTo(1));

        var sync = await service.GetDivergenceAsync(path, "main", "main");
        Assert.That(sync.Ahead, Is.EqualTo(0));
        Assert.That(sync.Behind, Is.EqualTo(0));
    }

    [Test]
    public void ParseDivergence_Accepts_Tab_Or_Space_Separated_Counts()
    {
        Assert.That(GitBranchService.ParseDivergence("3\t5"), Is.EqualTo(new BranchDivergence(3, 5)));
        Assert.That(GitBranchService.ParseDivergence("0 0\n"), Is.EqualTo(new BranchDivergence(0, 0)));
    }

    [Test]
    public async Task CheckoutOrCreateTracking_Creates_Local_From_Remote_Ref()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var (service, runner) = CreateService();
        await service.CreateBranchAsync(path, "feature", checkout: true);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "feature\n");
        await RunCommitAsync(runner, path, "feature commit");
        await service.CheckoutAsync(path, "main");

        // Simulate a fetched remote-tracking ref without a configured remote URL.
        await runner.RunAsync(path, ["update-ref", "refs/remotes/origin/feature", "refs/heads/feature"], options: null);
        await service.DeleteBranchAsync(path, "feature", force: true);

        await service.CheckoutOrCreateTrackingAsync(path, "feature", "origin/feature");

        var branches = await service.ListBranchesAsync(path);
        Assert.That(branches.Single(b => b.Name == "feature" && !b.IsRemote).IsCurrent, Is.True);
    }

    [Test]
    public async Task CheckoutOrCreateTracking_Checks_Out_Existing_Local()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var (service, _) = CreateService();
        await service.CreateBranchAsync(path, "feature", checkout: false);
        await service.CheckoutOrCreateTrackingAsync(path, "feature", "origin/feature");

        var branches = await service.ListBranchesAsync(path);
        Assert.That(branches.Single(b => b.Name == "feature" && !b.IsRemote).IsCurrent, Is.True);
    }

    [Test]
    public void ParseBranches_Reads_TipCommitterDate()
    {
        const char sep = '\u0001';
        var line =
            $"refs/heads/feature{sep}*{sep}origin/feature{sep}abc123{sep}2024-06-15T12:00:00+00:00\n";
        var parsed = GitBranchService.ParseBranches(line);
        Assert.That(parsed, Has.Count.EqualTo(1));
        Assert.That(parsed[0].Name, Is.EqualTo("feature"));
        Assert.That(parsed[0].IsCurrent, Is.True);
        Assert.That(parsed[0].TipOid, Is.EqualTo("abc123"));
        Assert.That(parsed[0].TipCommitterDate, Is.EqualTo(DateTimeOffset.Parse("2024-06-15T12:00:00+00:00")));
    }

    [Test]
    public void ParseBranches_Missing_Date_Falls_Back_To_MinValue()
    {
        const char sep = '\u0001';
        var line = $"refs/heads/main{sep} {sep}{sep}deadbeef\n";
        var parsed = GitBranchService.ParseBranches(line);
        Assert.That(parsed, Has.Count.EqualTo(1));
        Assert.That(parsed[0].TipCommitterDate, Is.EqualTo(DateTimeOffset.MinValue));
    }

    [Test]
    public async Task ListBranches_Orders_By_TipCommitterDate_Descending()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var (service, runner) = CreateService();

        // Pin main's tip date so later controlled commits can sort ahead of it.
        await RunCommitWithDateAsync(runner, path, "main tip", "2019-01-01T12:00:00+00:00", amend: true);

        await service.CreateBranchAsync(path, "older", checkout: true);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "older\n");
        await RunCommitWithDateAsync(runner, path, "older tip", "2020-01-01T12:00:00+00:00");

        await service.CreateBranchAsync(path, "newer", checkout: true);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "newer\n");
        await RunCommitWithDateAsync(runner, path, "newer tip", "2024-06-01T12:00:00+00:00");

        var locals = (await service.ListBranchesAsync(path))
            .Where(b => !b.IsRemote)
            .ToList();

        var newerIdx = locals.FindIndex(b => b.Name == "newer");
        var olderIdx = locals.FindIndex(b => b.Name == "older");
        var mainIdx = locals.FindIndex(b => b.Name == "main");
        Assert.That(newerIdx, Is.GreaterThanOrEqualTo(0));
        Assert.That(olderIdx, Is.GreaterThanOrEqualTo(0));
        Assert.That(mainIdx, Is.GreaterThanOrEqualTo(0));
        Assert.That(newerIdx, Is.LessThan(olderIdx));
        Assert.That(olderIdx, Is.LessThan(mainIdx));
        Assert.That(locals[newerIdx].TipCommitterDate, Is.GreaterThan(locals[olderIdx].TipCommitterDate));
        Assert.That(locals[olderIdx].TipCommitterDate, Is.GreaterThan(locals[mainIdx].TipCommitterDate));
    }

    private static async Task RunCommitAsync(GitProcessRunner runner, string path, string message)
    {
        await runner.RunAsync(path, ["add", "-A"], options: null);
        await runner.RunAsync(
            path,
            ["-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", message],
            options: null);
    }

    private static async Task RunCommitWithDateAsync(
        GitProcessRunner runner,
        string path,
        string message,
        string isoDate,
        bool amend = false)
    {
        await runner.RunAsync(path, ["add", "-A"], options: null);
        var args = new List<string>
        {
            "-c", "user.email=test@example.com",
            "-c", "user.name=Test",
            "commit",
            "-m", message,
        };
        if (amend)
            args.Add("--amend");

        await runner.RunAsync(
            path,
            args.ToArray(),
            new GitProcessOptions
            {
                ExtraEnvironment = new Dictionary<string, string?>
                {
                    ["GIT_AUTHOR_DATE"] = isoDate,
                    ["GIT_COMMITTER_DATE"] = isoDate,
                },
            });
    }
}

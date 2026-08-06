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
}

using CodeReviewr.AI;
using CodeReviewr.Core.AI;
using CodeReviewr.Git;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace CodeReviewr.AI.Tests;

[TestFixture]
public sealed class WorkingCopyMaterialiserTests
{
    [Test]
    public async Task WriteTree_StagedOnly_ExcludesUnstagedEdits()
    {
        using var builder = RepositoryBuilder.Create()
            .WithFile("tracked.txt", "base\n")
            .WithInitialCommit()
            .WithStagedChange("tracked.txt", "staged\n");
        var repo = builder.Build();

        File.WriteAllText(System.IO.Path.Combine(repo, "tracked.txt"), "unstaged\n");
        File.WriteAllText(System.IO.Path.Combine(repo, "untracked.txt"), "new\n");

        var materialiser = CreateMaterialiser();
        var stagedOid = await materialiser.WriteTreeAsync(repo, AiReviewScope.WorkingCopyStaged);
        var allOid = await materialiser.WriteTreeAsync(repo, AiReviewScope.WorkingCopyAll);

        Assert.That(stagedOid, Is.Not.EqualTo(allOid));

        var staged = await materialiser.MaterialiseAsync(repo, stagedOid);
        var all = await materialiser.MaterialiseAsync(repo, allOid);

        var stagedTracked = await File.ReadAllTextAsync(System.IO.Path.Combine(staged.Path, "tracked.txt"));
        var allTracked = await File.ReadAllTextAsync(System.IO.Path.Combine(all.Path, "tracked.txt"));

        Assert.That(stagedTracked, Is.EqualTo("staged\n"));
        Assert.That(allTracked, Is.EqualTo("unstaged\n"));
        Assert.That(File.Exists(System.IO.Path.Combine(staged.Path, "untracked.txt")), Is.False);
        Assert.That(File.Exists(System.IO.Path.Combine(all.Path, "untracked.txt")), Is.True);
    }

    [Test]
    public async Task Materialise_CachesByTreeOid()
    {
        using var builder = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit()
            .WithStagedChange("a.txt", "two\n");
        var repo = builder.Build();

        var materialiser = CreateMaterialiser();
        var treeOid = await materialiser.WriteTreeAsync(repo, AiReviewScope.WorkingCopyStaged);
        var first = await materialiser.MaterialiseAsync(repo, treeOid);
        var second = await materialiser.MaterialiseAsync(repo, treeOid);

        Assert.That(first.WasCacheHit, Is.False);
        Assert.That(second.WasCacheHit, Is.True);
        Assert.That(second.Path, Is.EqualTo(first.Path));
    }

    private static WorkingCopyMaterialiser CreateMaterialiser()
    {
        var runner = new GitProcessRunner();
        var gates = new RepositoryGateProvider(runner);
        return new WorkingCopyMaterialiser(runner, gates, NullLogger<WorkingCopyMaterialiser>.Instance);
    }
}

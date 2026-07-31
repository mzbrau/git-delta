using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Caching;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CodeReviewr.IntegrationTests;

public sealed class DiscardRestoreTests
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
    public async Task Discard_Tracked_File_Restores_Worktree_To_Head()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "original\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "changed\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var discard = sp.GetRequiredService<IGitDiscardService>();

        await discard.DiscardFileAsync(path, FilePath.From("a.txt"));

        Assert.That(await File.ReadAllTextAsync(Path.Combine(path, "a.txt")), Is.EqualTo("original\n"));
        Assert.That(discard.RecentlyDiscarded, Has.Count.EqualTo(1));

        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Unstaged, Is.Empty);
    }

    [Test]
    public async Task Discard_Staged_File_Restores_Index_And_Worktree_To_Head()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "original\n")
            .WithInitialCommit("init")
            .WithStagedChange("a.txt", "staged\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var discard = sp.GetRequiredService<IGitDiscardService>();
        var statusSvc = sp.GetRequiredService<IGitStatusService>();

        var before = await statusSvc.GetStatusAsync(path);
        Assert.That(before.Staged, Is.Not.Empty);

        await discard.DiscardStagedFileAsync(path, FilePath.From("a.txt"));

        Assert.That(await File.ReadAllTextAsync(Path.Combine(path, "a.txt")), Is.EqualTo("original\n"));
        var after = await statusSvc.GetStatusAsync(path);
        Assert.That(after.Staged, Is.Empty);
        Assert.That(after.Unstaged, Is.Empty);
    }

    [Test]
    public async Task Discard_Untracked_File_Deletes_It_And_Undo_Restores()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("tracked.txt", "ok\n")
            .WithInitialCommit("init")
            .WithUntracked("new.txt", "fresh\n");
        var path = repo.Build();
        var newPath = Path.Combine(path, "new.txt");
        Assert.That(File.Exists(newPath), Is.True);

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var discard = sp.GetRequiredService<IGitDiscardService>();

        await discard.DiscardFileAsync(path, FilePath.From("new.txt"));
        Assert.That(File.Exists(newPath), Is.False);

        var entry = discard.RecentlyDiscarded.Single(e => e.Path.Value == "new.txt");
        Assert.That(entry.WasUntracked, Is.True);

        await discard.RestoreDiscardedAsync(path, entry);
        Assert.That(await File.ReadAllTextAsync(newPath), Is.EqualTo("fresh\n"));
    }

    [Test]
    public async Task Discard_Patch_Removes_One_Hunk_Leaves_Others()
    {
        var content = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line{i}")) + "\n";
        var changed = content.Replace("line5", "LINE5").Replace("line30", "LINE30");

        using var repo = RepositoryBuilder.Create()
            .WithFile("multi.txt", content)
            .WithInitialCommit("init")
            .WithFile("multi.txt", changed);
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffs = sp.GetRequiredService<IGitDiffService>();
        var discard = sp.GetRequiredService<IGitDiscardService>();

        var diff = await diffs.GetDiffAsync(path, FilePath.From("multi.txt"), DiffTarget.IndexToWorktree, DiffOptions.Default);
        Assert.That(diff.Hunks.Count, Is.GreaterThanOrEqualTo(2));

        var patch = PatchSynthesizer.SynthesizeHunks(diff, [0]);
        await discard.DiscardPatchAsync(path, patch);

        var after = await File.ReadAllTextAsync(Path.Combine(path, "multi.txt"));
        Assert.That(after, Does.Contain("line5"));
        Assert.That(after, Does.Contain("LINE30"));

        var remaining = await diffs.GetDiffAsync(path, FilePath.From("multi.txt"), DiffTarget.IndexToWorktree, DiffOptions.Default);
        Assert.That(remaining.Hunks, Is.Not.Empty);
        Assert.That(remaining.Hunks.Count, Is.LessThan(diff.Hunks.Count));
    }

    [Test]
    public async Task Restore_Tracked_Discard_Brings_Back_Edits()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "original\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "changed\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var discard = sp.GetRequiredService<IGitDiscardService>();

        await discard.DiscardFileAsync(path, FilePath.From("a.txt"));
        var entry = discard.RecentlyDiscarded.Single();
        await discard.RestoreDiscardedAsync(path, entry);

        Assert.That(await File.ReadAllTextAsync(Path.Combine(path, "a.txt")), Is.EqualTo("changed\n"));
    }
}

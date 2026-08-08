using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.AI;
using GitDelta.Core.Caching;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace GitDelta.IntegrationTests;

public sealed class MagicCommitExecutorTests
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

    private static RepositoryBuilder TwoHunkRepo(int fillerLines = 12)
    {
        // Use LF only — AppendLine emits CRLF on Windows and breaks git apply --cached.
        var original = string.Join('\n', Enumerable.Range(1, fillerLines).Select(i => $"line{i}")) + "\n";
        var lines = original.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        lines[0] = "CHANGED_TOP";
        lines[^1] = "CHANGED_BOTTOM";
        var modified = string.Join('\n', lines) + "\n";

        return RepositoryBuilder.Create()
            .WithFile("file.cs", original)
            .WithInitialCommit("init")
            .WithFile("file.cs", modified);
    }

    [Test]
    public async Task Splits_Two_Hunks_Of_Same_File_Into_Two_Commits()
    {
        using var repo = TwoHunkRepo();
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffService = sp.GetRequiredService<IGitDiffService>();
        var staging = sp.GetRequiredService<IGitStagingService>();
        var commit = sp.GetRequiredService<IGitCommitService>();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var options = DiffOptions.Default;
        var fileDiff = await diffService.GetDiffAsync(
            path, FilePath.From("file.cs"), DiffTarget.IndexToWorktree, options);
        Assert.That(fileDiff.Hunks.Count, Is.GreaterThanOrEqualTo(2), "Expected at least two hunks");

        var inventory = MagicCommitInventory.Build([fileDiff]);
        Assert.That(inventory.Count, Is.GreaterThanOrEqualTo(2));

        // First commit: last hunk only; second commit: remaining earlier hunk(s).
        var last = inventory[^1];
        var earlier = inventory.Take(inventory.Count - 1).ToList();
        var plan = new MagicCommitPlan(
        [
            new MagicCommitPlanEntry("change bottom", [last.Id]),
            new MagicCommitPlanEntry("change top", earlier.Select(i => i.Id).ToList()),
        ]);

        var executor = new MagicCommitExecutor(diffService, staging, commit, history);
        var result = await executor.ExecuteAsync(path, inventory, plan, options, noVerify: true, progress: null);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Commits, Has.Count.EqualTo(2));
        Assert.That(result.Commits[0].Subject, Is.EqualTo("change bottom"));
        Assert.That(result.Commits[1].Subject, Is.EqualTo("change top"));

        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Staged, Is.Empty);
        Assert.That(status.Unstaged, Is.Empty);

        var log = await history.ListCommitsAsync(path, skip: 0, take: 3);
        Assert.That(log[0].Subject, Is.EqualTo("change top"));
        Assert.That(log[1].Subject, Is.EqualTo("change bottom"));
    }

    [Test]
    public async Task Splits_Two_Hunks_With_NonDefault_ContextLines()
    {
        // Enough filler that -U10 still yields two separate hunks.
        using var repo = TwoHunkRepo(fillerLines: 40);
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffService = sp.GetRequiredService<IGitDiffService>();
        var staging = sp.GetRequiredService<IGitStagingService>();
        var commit = sp.GetRequiredService<IGitCommitService>();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var options = new DiffOptions(ContextLines: 10);
        var fileDiff = await diffService.GetDiffAsync(
            path, FilePath.From("file.cs"), DiffTarget.IndexToWorktree, options);
        Assert.That(fileDiff.Hunks.Count, Is.GreaterThanOrEqualTo(2), "Expected at least two hunks with -U10");

        var inventory = MagicCommitInventory.Build([fileDiff]);
        Assert.That(inventory.Count, Is.GreaterThanOrEqualTo(2));

        var last = inventory[^1];
        var earlier = inventory.Take(inventory.Count - 1).ToList();
        var plan = new MagicCommitPlan(
        [
            new MagicCommitPlanEntry("change bottom", [last.Id]),
            new MagicCommitPlanEntry("change top", earlier.Select(i => i.Id).ToList()),
        ]);

        var executor = new MagicCommitExecutor(diffService, staging, commit, history);
        var result = await executor.ExecuteAsync(path, inventory, plan, options, noVerify: true, progress: null);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Commits, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Rematch_Fails_When_Executor_Uses_Different_ContextLines()
    {
        // Single-hunk file (README-style): inventory at -U10, rematch at Default (-U3) must fail.
        using var repo = RepositoryBuilder.Create()
            .WithFile("README.md", string.Join('\n', Enumerable.Range(1, 15).Select(i => $"line{i}")) + "\n")
            .WithInitialCommit("init")
            .WithFile("README.md", string.Join('\n', Enumerable.Range(1, 15).Select(i => i == 8 ? "CHANGED" : $"line{i}")) + "\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffService = sp.GetRequiredService<IGitDiffService>();
        var staging = sp.GetRequiredService<IGitStagingService>();
        var commit = sp.GetRequiredService<IGitCommitService>();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var inventoryOptions = new DiffOptions(ContextLines: 10);
        var fileDiff = await diffService.GetDiffAsync(
            path, FilePath.From("README.md"), DiffTarget.IndexToWorktree, inventoryOptions);
        Assert.That(fileDiff.Hunks.Count, Is.EqualTo(1));
        Assert.That(fileDiff.Hunks[0].Header, Does.Contain("15"));

        var inventory = MagicCommitInventory.Build([fileDiff]);
        Assert.That(inventory, Has.Count.EqualTo(1));

        var plan = new MagicCommitPlan([new MagicCommitPlanEntry("update readme", [inventory[0].Id])]);

        var executor = new MagicCommitExecutor(diffService, staging, commit, history);
        var result = await executor.ExecuteAsync(
            path, inventory, plan, DiffOptions.Default, noVerify: true, progress: null);

        Assert.That(result.Error, Does.Contain("Could not rematch hunk"));
        Assert.That(result.Error, Does.Not.Contain("after earlier commits"));
        Assert.That(result.Commits, Is.Empty);
    }

    [Test]
    public async Task Rematch_Succeeds_When_Executor_Uses_Matching_ContextLines()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("README.md", string.Join('\n', Enumerable.Range(1, 15).Select(i => $"line{i}")) + "\n")
            .WithInitialCommit("init")
            .WithFile("README.md", string.Join('\n', Enumerable.Range(1, 15).Select(i => i == 8 ? "CHANGED" : $"line{i}")) + "\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffService = sp.GetRequiredService<IGitDiffService>();
        var staging = sp.GetRequiredService<IGitStagingService>();
        var commit = sp.GetRequiredService<IGitCommitService>();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var options = new DiffOptions(ContextLines: 10);
        var fileDiff = await diffService.GetDiffAsync(
            path, FilePath.From("README.md"), DiffTarget.IndexToWorktree, options);
        var inventory = MagicCommitInventory.Build([fileDiff]);
        var plan = new MagicCommitPlan([new MagicCommitPlanEntry("update readme", [inventory[0].Id])]);

        var executor = new MagicCommitExecutor(diffService, staging, commit, history);
        var result = await executor.ExecuteAsync(path, inventory, plan, options, noVerify: true, progress: null);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Commits, Has.Count.EqualTo(1));
        Assert.That(result.Commits[0].Subject, Is.EqualTo("update readme"));
    }

    [Test]
    public async Task Commits_Tracked_Modification_And_Untracked_File()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("tracked.txt", "original\n")
            .WithInitialCommit("init")
            .WithFile("tracked.txt", "modified\n")
            .WithUntracked("new.txt", "brand new\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffService = sp.GetRequiredService<IGitDiffService>();
        var staging = sp.GetRequiredService<IGitStagingService>();
        var commit = sp.GetRequiredService<IGitCommitService>();
        var history = sp.GetRequiredService<IGitHistoryService>();
        var statusService = sp.GetRequiredService<IGitStatusService>();

        var options = DiffOptions.Default;
        var trackedDiff = await diffService.GetDiffAsync(
            path, FilePath.From("tracked.txt"), DiffTarget.IndexToWorktree, options);
        var untrackedDiff = UntrackedFileDiff.Create(
            FilePath.From("new.txt"),
            await File.ReadAllTextAsync(Path.Combine(path, "new.txt")),
            DiffTarget.IndexToWorktree);

        var inventory = MagicCommitInventory.Build([trackedDiff, untrackedDiff]);
        Assert.That(inventory, Has.Count.EqualTo(2));
        Assert.That(inventory.Count(i => i.WholeFile), Is.EqualTo(1));
        Assert.That(inventory.Single(i => i.WholeFile).Path, Is.EqualTo("new.txt"));

        var plan = new MagicCommitPlan(
        [
            new MagicCommitPlanEntry("update tracked", [inventory.Single(i => i.Path == "tracked.txt").Id]),
            new MagicCommitPlanEntry("add new file", [inventory.Single(i => i.Path == "new.txt").Id]),
        ]);

        var executor = new MagicCommitExecutor(diffService, staging, commit, history);
        var result = await executor.ExecuteAsync(path, inventory, plan, options, noVerify: true, progress: null);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Commits, Has.Count.EqualTo(2));

        var status = await statusService.GetStatusAsync(path);
        Assert.That(status.Staged, Is.Empty);
        Assert.That(status.Unstaged, Is.Empty);

        var log = await history.ListCommitsAsync(path, skip: 0, take: 3);
        Assert.That(log[0].Subject, Is.EqualTo("add new file"));
        Assert.That(log[1].Subject, Is.EqualTo("update tracked"));
    }

    [Test]
    public async Task StagedOnly_Inventory_Excludes_Unstaged_Hunks_In_Same_File()
    {
        // Regression for PR #23: rebuilding inventory from IndexToWorktree after unstage
        // would pull unstaged hunks into a staged-only Magic Commit plan.
        using var repo = TwoHunkRepo(fillerLines: 40);
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffService = sp.GetRequiredService<IGitDiffService>();
        var staging = sp.GetRequiredService<IGitStagingService>();

        var options = DiffOptions.Default;
        var worktreeDiff = await diffService.GetDiffAsync(
            path, FilePath.From("file.cs"), DiffTarget.IndexToWorktree, options);
        Assert.That(worktreeDiff.Hunks.Count, Is.GreaterThanOrEqualTo(2));

        // Stage only the first hunk; leave the rest unstaged.
        var firstHunkPatch = PatchSynthesizer.SynthesizeHunks(worktreeDiff, [0]);
        await staging.StagePatchAsync(path, firstHunkPatch);

        var stagedDiff = await diffService.GetDiffAsync(
            path, FilePath.From("file.cs"), DiffTarget.HeadToIndex, options);
        Assert.That(stagedDiff.Hunks.Count, Is.EqualTo(1), "Only the staged hunk should appear in HeadToIndex");

        var stagedInventory = MagicCommitInventory.Build([stagedDiff]);
        Assert.That(stagedInventory, Has.Count.EqualTo(1));

        // Unstage for executor rematch (product does this). IndexToWorktree then shows ALL
        // hunks again — the old bug rebuilt inventory from this and would plan both.
        await staging.UnstageFilesAsync(path, [FilePath.From("file.cs")]);
        var afterUnstageWorktree = await diffService.GetDiffAsync(
            path, FilePath.From("file.cs"), DiffTarget.IndexToWorktree, options);
        var worktreeInventory = MagicCommitInventory.Build([afterUnstageWorktree]);
        Assert.That(worktreeInventory.Count, Is.GreaterThan(stagedInventory.Count),
            "After unstage, IndexToWorktree has every hunk; staged-only must keep the HeadToIndex inventory");

        // Execute using the staged-only inventory (as ConfirmMagicCommitAsync does after the fix).
        var plan = new MagicCommitPlan([new MagicCommitPlanEntry("stage first hunk only", [stagedInventory[0].Id])]);
        var executor = new MagicCommitExecutor(
            diffService, staging, sp.GetRequiredService<IGitCommitService>(), sp.GetRequiredService<IGitHistoryService>());
        var result = await executor.ExecuteAsync(
            path, stagedInventory, plan, options, noVerify: true, progress: null);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Commits, Has.Count.EqualTo(1));

        // The other hunk remains uncommitted in the worktree.
        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Unstaged.Any(s => s.Path.Value == "file.cs"), Is.True);
    }

    [Test]
    public async Task Staged_Added_File_Survives_Unstage_And_Execute()
    {
        // Product unstages inventory paths before execute. Staged Added files become untracked;
        // they must be whole-file items staged via git add, not hunk rematch.
        const string lfsPointer =
            "version https://git-lfs.github.com/spec/v1\n" +
            "oid sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\n" +
            "size 1234\n";

        using var repo = RepositoryBuilder.Create()
            .WithFile("tracked.txt", "keep\n")
            .WithInitialCommit("init")
            .WithStagedChange("asset.png", lfsPointer);
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffService = sp.GetRequiredService<IGitDiffService>();
        var staging = sp.GetRequiredService<IGitStagingService>();
        var commit = sp.GetRequiredService<IGitCommitService>();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var options = DiffOptions.Default;
        var addedDiff = await diffService.GetDiffAsync(
            path, FilePath.From("asset.png"), DiffTarget.HeadToIndex, options);
        Assert.That(addedDiff.Change, Is.EqualTo(ChangeKind.Added));
        Assert.That(addedDiff.Hunks, Is.Not.Empty, "LFS pointer text has hunks");

        var inventory = MagicCommitInventory.Build([addedDiff]);
        Assert.That(inventory, Has.Count.EqualTo(1));
        Assert.That(inventory[0].WholeFile, Is.True);

        await staging.UnstageFilesAsync(path, [FilePath.From("asset.png")]);

        var plan = new MagicCommitPlan([new MagicCommitPlanEntry("add LFS asset", [inventory[0].Id])]);
        var executor = new MagicCommitExecutor(diffService, staging, commit, history);
        var result = await executor.ExecuteAsync(path, inventory, plan, options, noVerify: true, progress: null);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Commits, Has.Count.EqualTo(1));
        Assert.That(result.Commits[0].Subject, Is.EqualTo("add LFS asset"));

        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Staged, Is.Empty);
        Assert.That(status.Unstaged, Is.Empty);
    }

    [Test]
    public async Task Ignored_Staged_Added_File_Survives_Unstage_And_Execute()
    {
        // Product unstages inventory paths before execute. A force-staged ignored file becomes
        // ignored-untracked; plain git add fails — Magic Commit must re-stage with -f.
        using var repo = RepositoryBuilder.Create()
            .WithFile("tracked.txt", "keep\n")
            .WithFile(".gitignore", "*.png\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffService = sp.GetRequiredService<IGitDiffService>();
        var staging = sp.GetRequiredService<IGitStagingService>();
        var commit = sp.GetRequiredService<IGitCommitService>();
        var history = sp.GetRequiredService<IGitHistoryService>();

        var assetFull = System.IO.Path.Combine(path, "asset.png");
        await File.WriteAllTextAsync(assetFull, "png-bytes-or-lfs-pointer\n");
        await staging.StageFileAsync(path, FilePath.From("asset.png"), force: true);

        var options = DiffOptions.Default;
        var addedDiff = await diffService.GetDiffAsync(
            path, FilePath.From("asset.png"), DiffTarget.HeadToIndex, options);
        Assert.That(addedDiff.Change, Is.EqualTo(ChangeKind.Added));

        var inventory = MagicCommitInventory.Build([addedDiff]);
        Assert.That(inventory, Has.Count.EqualTo(1));
        Assert.That(inventory[0].WholeFile, Is.True);

        await staging.UnstageFilesAsync(path, [FilePath.From("asset.png")]);

        // Sanity: without -f, re-add fails once the path is ignored-untracked.
        Assert.ThrowsAsync<GitException>(async () =>
            await staging.StageFileAsync(path, FilePath.From("asset.png")));

        var plan = new MagicCommitPlan([new MagicCommitPlanEntry("add ignored asset", [inventory[0].Id])]);
        var executor = new MagicCommitExecutor(diffService, staging, commit, history);
        var result = await executor.ExecuteAsync(path, inventory, plan, options, noVerify: true, progress: null);

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Commits, Has.Count.EqualTo(1));
        Assert.That(result.Commits[0].Subject, Is.EqualTo("add ignored asset"));

        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Staged, Is.Empty);
        Assert.That(status.Unstaged, Is.Empty);
    }
}
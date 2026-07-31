using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Caching;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CodeReviewr.IntegrationTests;

public sealed class StagingRoundTripTests
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
    public async Task Unstage_File_Moves_Back_To_Unstaged()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init")
            .WithStagedChange("a.txt", "two\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var staging = sp.GetRequiredService<IGitStagingService>();
        var status = sp.GetRequiredService<IGitStatusService>();

        var before = await status.GetStatusAsync(path);
        Assert.That(before.Staged.Any(s => s.Path.Value == "a.txt"), Is.True);

        await staging.UnstageFileAsync(path, FilePath.From("a.txt"));

        var after = await status.GetStatusAsync(path);
        Assert.That(after.Staged.Any(s => s.Path.Value == "a.txt"), Is.False);
        Assert.That(after.Unstaged.Any(s => s.Path.Value == "a.txt"), Is.True);
    }

    [Test]
    public async Task Stage_Multiple_Files_In_One_Call()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "a\n")
            .WithFile("b.txt", "b\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "A\n")
            .WithFile("b.txt", "B\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var staging = sp.GetRequiredService<IGitStagingService>();

        await staging.StageFilesAsync(path, [FilePath.From("a.txt"), FilePath.From("b.txt")]);

        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Staged.Select(s => s.Path.Value), Is.EquivalentTo(new[] { "a.txt", "b.txt" }));
        Assert.That(status.Unstaged, Is.Empty);
    }

    [Test]
    public async Task Stage_And_Unstage_Hunk_Via_Synthesized_Patch()
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
        var staging = sp.GetRequiredService<IGitStagingService>();
        var status = sp.GetRequiredService<IGitStatusService>();

        var worktreeDiff = await diffs.GetDiffAsync(path, FilePath.From("multi.txt"), DiffTarget.IndexToWorktree, DiffOptions.Default);
        Assert.That(worktreeDiff.Hunks.Count, Is.GreaterThanOrEqualTo(2));

        var patch = PatchSynthesizer.SynthesizeHunks(worktreeDiff, [0]);
        await staging.StagePatchAsync(path, patch);

        var mid = await status.GetStatusAsync(path);
        Assert.That(mid.Staged.Any(s => s.Path.Value == "multi.txt"), Is.True);
        Assert.That(mid.Unstaged.Any(s => s.Path.Value == "multi.txt"), Is.True);

        var stagedDiff = await diffs.GetDiffAsync(path, FilePath.From("multi.txt"), DiffTarget.HeadToIndex, DiffOptions.Default);
        Assert.That(stagedDiff.Hunks, Is.Not.Empty);

        var unstagePatch = PatchSynthesizer.SynthesizeHunks(stagedDiff, [0]);
        await staging.UnstagePatchAsync(path, unstagePatch);

        var after = await status.GetStatusAsync(path);
        Assert.That(after.Staged.Any(s => s.Path.Value == "multi.txt"), Is.False);
        Assert.That(after.Unstaged.Any(s => s.Path.Value == "multi.txt"), Is.True);
    }

    [Test]
    public async Task Stage_Selected_Lines_Via_SynthesizeLines()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "keep\nold\nkeep2\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "keep\nnew\nkeep2\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffs = sp.GetRequiredService<IGitDiffService>();
        var staging = sp.GetRequiredService<IGitStagingService>();

        var diff = await diffs.GetDiffAsync(path, FilePath.From("a.txt"), DiffTarget.IndexToWorktree, DiffOptions.Default);
        var removed = diff.Hunks[0].Lines
            .Select((l, i) => (l, i))
            .First(x => x.l.Kind == DiffLineKind.Removed);
        var added = diff.Hunks[0].Lines
            .Select((l, i) => (l, i))
            .First(x => x.l.Kind == DiffLineKind.Added);

        var patch = PatchSynthesizer.SynthesizeLines(diff, [
            new LineSelection(0, removed.i),
            new LineSelection(0, added.i),
        ]);
        await staging.StagePatchAsync(path, patch);

        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Staged.Any(s => s.Path.Value == "a.txt"), Is.True);
        Assert.That(status.Unstaged.Any(s => s.Path.Value == "a.txt"), Is.False);
    }
}

using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Caching;
using GitDelta.Diff;
using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace GitDelta.IntegrationTests;

public sealed class WalkingSkeletonTests
{
    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDiffCache, MemoryDiffCache>();
        services.AddGitDeltaGit();
        services.AddGitDeltaDiff();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Open_Status_Diff_RoundTrip()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("readme.txt", "hello\n")
            .WithInitialCommit("init")
            .WithFile("readme.txt", "hello world\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        var env = sp.GetRequiredService<IGitEnvironment>();
        var info = await env.DetectAsync();
        Assert.That(info.Version.MeetsMinimum, Is.True);

        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Unstaged, Has.Count.GreaterThanOrEqualTo(1));

        var file = status.Unstaged[0].Path;
        var diff = await sp.GetRequiredService<IGitDiffService>()
            .GetWorkingCopyDiffAsync(path, file, DiffTarget.IndexToWorktree, DiffOptions.Default);
        Assert.That(diff.Hunks, Is.Not.Empty);

        var rows = UnifiedRowProjector.Project(diff);
        Assert.That(rows, Is.Not.Empty);
    }

    [Test]
    public async Task Cached_Diff_Performs_Zero_Git_Invocations_On_Reselect()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "two\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var diffs = sp.GetRequiredService<IGitDiffService>();
        var file = FilePath.From("a.txt");

        _ = await diffs.GetWorkingCopyDiffAsync(path, file, DiffTarget.IndexToWorktree, DiffOptions.Default);

        var cache = sp.GetRequiredService<IDiffCache>();
        var hitsBefore = cache.HitCount;
        var missesBefore = cache.MissCount;

        _ = await diffs.GetWorkingCopyDiffAsync(path, file, DiffTarget.IndexToWorktree, DiffOptions.Default);

        Assert.That(cache.HitCount, Is.GreaterThan(hitsBefore), "Second identical diff should hit the content-addressed cache");
        Assert.That(cache.MissCount, Is.EqualTo(missesBefore));
    }

    [Test]
    public async Task Stage_File_Then_Commit()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "two\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        await sp.GetRequiredService<IGitStagingService>().StageFileAsync(path, FilePath.From("a.txt"));
        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Staged.Any(s => s.Path.Value == "a.txt"), Is.True);

        await sp.GetRequiredService<IGitCommitService>()
            .CommitAsync(path, "update a", amend: false, noVerify: false, hookOutput: null);
        status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.Staged, Is.Empty);
        Assert.That(status.Unstaged, Is.Empty);
    }

    [Test]
    public async Task Conflicted_Repository_Is_Never_Shown_As_Clean()
    {
        // Simpler conflict setup without the flaky builder path
        using var repo = RepositoryBuilder.Create()
            .WithFile("c.txt", "base\n")
            .WithInitialCommit("base");
        var path = repo.Build();
        repo.RunGit("checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(path, "c.txt"), "feature\n");
        repo.RunGit("add", "c.txt");
        repo.RunGit("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "feature");
        repo.RunGit("checkout", "main");
        File.WriteAllText(Path.Combine(path, "c.txt"), "main\n");
        repo.RunGit("add", "c.txt");
        repo.RunGit("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "main");
        try { repo.RunGit("merge", "--no-edit", "feature"); } catch { /* expected conflict */ }

        Assert.That(
            File.Exists(Path.Combine(path, ".git", "MERGE_HEAD")),
            Is.True,
            "Merge must leave MERGE_HEAD so the conflict setup is valid before status parsing");

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var status = await sp.GetRequiredService<IGitStatusService>().GetStatusAsync(path);
        Assert.That(status.InProgress, Is.EqualTo(InProgressOperation.Merge));
        Assert.That(status.Conflicted, Is.Not.Empty);
    }
}

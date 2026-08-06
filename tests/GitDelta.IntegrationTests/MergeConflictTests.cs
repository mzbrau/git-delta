using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Caching;
using GitDelta.Diff;
using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace GitDelta.IntegrationTests;

public sealed class MergeConflictTests
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

    private static async Task StartConflictingMergeAsync(IGitProcessRunner runner, string path)
    {
        var result = await runner.RunAsync(
            path,
            ["merge", "--no-edit", "feature"],
            new GitProcessOptions { AllowNonZeroExitCode = true });
        Assert.That(result.Succeeded, Is.False, "Expected a conflicting merge.");
    }

    [Test]
    public async Task Merge_Conflict_Abort_Clears_InProgress()
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

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var runner = sp.GetRequiredService<IGitProcessRunner>();
        var conflicts = sp.GetRequiredService<IGitConflictService>();

        await StartConflictingMergeAsync(runner, path);
        Assert.That(await conflicts.DetectInProgressAsync(path), Is.EqualTo(InProgressOperation.Merge));

        await conflicts.AbortAsync(path);
        Assert.That(await conflicts.DetectInProgressAsync(path), Is.EqualTo(InProgressOperation.None));
        Assert.That(File.ReadAllText(Path.Combine(path, "a.txt")), Is.EqualTo("main\n"));
    }

    [Test]
    public async Task Merge_Conflict_Continue_After_Resolve()
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

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var runner = sp.GetRequiredService<IGitProcessRunner>();
        var conflicts = sp.GetRequiredService<IGitConflictService>();

        await StartConflictingMergeAsync(runner, path);
        Assert.That(await conflicts.DetectInProgressAsync(path), Is.EqualTo(InProgressOperation.Merge));

        File.WriteAllText(Path.Combine(path, "a.txt"), "resolved\n");
        await conflicts.MarkResolvedAsync(path, FilePath.From("a.txt"));
        await conflicts.ContinueAsync(path);

        Assert.That(await conflicts.DetectInProgressAsync(path), Is.EqualTo(InProgressOperation.None));
        Assert.That(File.ReadAllText(Path.Combine(path, "a.txt")), Is.EqualTo("resolved\n"));

        var log = repo.RunGit("log", "--oneline", "-1");
        Assert.That(log, Does.Contain("Merge").Or.Contain("feature"));
    }
}

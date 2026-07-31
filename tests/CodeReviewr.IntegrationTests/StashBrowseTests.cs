using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Caching;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CodeReviewr.IntegrationTests;

public sealed class StashBrowseTests
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
    public async Task Stash_Push_List_Files_Patch_And_Apply()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "original\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "changed\n")
            .WithUntracked("new.txt", "fresh\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var stash = sp.GetRequiredService<IGitStashService>();
        var status = sp.GetRequiredService<IGitStatusService>();

        await stash.StashPushAsync(path, "test stash", includeUntracked: true);

        var afterPush = await status.GetStatusAsync(path);
        Assert.That(afterPush.Unstaged, Is.Empty);
        Assert.That(afterPush.Staged, Is.Empty);
        Assert.That(File.Exists(Path.Combine(path, "new.txt")), Is.False);

        var list = await stash.ListStashesAsync(path);
        Assert.That(list, Is.Not.Empty);
        Assert.That(list[0].Index, Is.EqualTo(0));
        Assert.That(list[0].Message, Does.Contain("test stash").Or.Contain("WIP"));

        var files = await stash.GetStashFilesAsync(path, list[0].Index);
        Assert.That(files.Any(f => f.Path.Value == "a.txt"), Is.True);
        Assert.That(files.Any(f => f.Path.Value == "new.txt"), Is.True);

        var patch = await stash.GetStashPatchAsync(path, list[0].Index, FilePath.From("a.txt"), DiffOptions.Default);
        Assert.That(patch, Does.Contain("changed").Or.Contain("a.txt"));

        var untrackedPatch = await stash.GetStashPatchAsync(path, list[0].Index, FilePath.From("new.txt"), DiffOptions.Default);
        Assert.That(untrackedPatch, Does.Contain("fresh").Or.Contain("new.txt"));

        await stash.ApplyStashAsync(path, list[0].Index);

        Assert.That(await File.ReadAllTextAsync(Path.Combine(path, "a.txt")), Is.EqualTo("changed\n"));
        // apply keeps the stash
        var stillListed = await stash.ListStashesAsync(path);
        Assert.That(stillListed, Is.Not.Empty);
    }

    [Test]
    public async Task Stash_Pop_Restores_Latest_And_Removes_It()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "original\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "changed\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var stash = sp.GetRequiredService<IGitStashService>();

        await stash.StashPushAsync(path, "to pop", includeUntracked: false);
        Assert.That(await stash.ListStashesAsync(path), Is.Not.Empty);

        await stash.StashPopAsync(path);

        Assert.That(await File.ReadAllTextAsync(Path.Combine(path, "a.txt")), Is.EqualTo("changed\n"));
        Assert.That(await stash.ListStashesAsync(path), Is.Empty);
    }

    [Test]
    public async Task Stash_Drop_Removes_Entry_Without_Restoring()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "original\n")
            .WithInitialCommit("init")
            .WithFile("a.txt", "changed\n");
        var path = repo.Build();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var stash = sp.GetRequiredService<IGitStashService>();

        await stash.StashPushAsync(path, "to drop", includeUntracked: false);
        var list = await stash.ListStashesAsync(path);
        Assert.That(list, Is.Not.Empty);

        await stash.DropStashAsync(path, list[0].Index);

        Assert.That(await stash.ListStashesAsync(path), Is.Empty);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(path, "a.txt")), Is.EqualTo("original\n"));
    }

    [Test]
    public async Task GetRemoteUrl_Returns_Configured_Origin()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "x\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        // Add a fake origin remote
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = path,
            ArgumentList = { "remote", "add", "origin", "git@github.com:acme/demo.git" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            await p.WaitForExitAsync();
            Assert.That(p.ExitCode, Is.EqualTo(0));
        }

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var remotes = sp.GetRequiredService<IGitRemoteService>();

        var url = await remotes.GetRemoteUrlAsync(path);
        Assert.That(url, Is.EqualTo("git@github.com:acme/demo.git"));
    }
}

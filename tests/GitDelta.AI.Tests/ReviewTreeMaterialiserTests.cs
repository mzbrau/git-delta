using GitDelta.Core.Abstractions;
using GitDelta.Git;
using GitDelta.Review;
using GitDelta.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace GitDelta.AI.Tests;

public sealed class ReviewTreeMaterialiserTests
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddGitDeltaGit();
        services.AddGitDeltaReview();
        return services.BuildServiceProvider();
    }

    private static ReviewTreeMaterialiser CreateMaterialiser(ServiceProvider sp) => new(
        sp.GetRequiredService<IGitProcessRunner>(),
        sp.GetRequiredService<IRepositoryGateProvider>(),
        sp.GetRequiredService<IReviewTreeFactory>(),
        NullLogger<ReviewTreeMaterialiser>.Instance);

    [Test]
    public async Task MaterialiseAsync_ExportsCommitContentToDisk()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("src/App.cs", "class App {}\n")
            .WithFile("README.md", "hello\n")
            .WithInitialCommit("root");
        var repoPath = repo.Build();
        var head = repo.RunGit("rev-parse", "HEAD").Trim();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var materialiser = CreateMaterialiser(sp);

        try
        {
            var result = await materialiser.MaterialiseAsync(repoPath, head);

            Assert.That(result.WasCacheHit, Is.False);
            Assert.That(result.MissingExportIgnorePaths, Is.Empty);
            Assert.That(Directory.Exists(result.Path), Is.True);

            var exportedFile = Path.Combine(result.Path, "src", "App.cs");
            Assert.That(File.Exists(exportedFile), Is.True);
            Assert.That(await File.ReadAllTextAsync(exportedFile), Is.EqualTo("class App {}\n"));
            Assert.That(result.Tree.MaterialisedPath, Is.EqualTo(result.Path));
        }
        finally
        {
            TryDeleteExport(head);
        }
    }

    [Test]
    public async Task MaterialiseAsync_SecondCallForSameSha_IsCacheHit()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("README.md", "hello\n")
            .WithInitialCommit("root");
        var repoPath = repo.Build();
        var head = repo.RunGit("rev-parse", "HEAD").Trim();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var materialiser = CreateMaterialiser(sp);

        try
        {
            var first = await materialiser.MaterialiseAsync(repoPath, head);
            Assert.That(first.WasCacheHit, Is.False);

            var second = await materialiser.MaterialiseAsync(repoPath, head);
            Assert.That(second.WasCacheHit, Is.True);
            Assert.That(second.Path, Is.EqualTo(first.Path));
            Assert.That(File.Exists(Path.Combine(second.Path, "README.md")), Is.True);
        }
        finally
        {
            TryDeleteExport(head);
        }
    }

    [Test]
    public async Task MaterialiseAsync_DoesNotMutateUserBranchOrWorktree()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("src/App.cs", "class App {}\n")
            .WithInitialCommit("root")
            .WithFile("src/App.cs", "class App { void Run() {} }\n")
            .WithCommit("feature work");
        var repoPath = repo.Build();
        var headBefore = repo.RunGit("rev-parse", "HEAD").Trim();
        var branchBefore = repo.RunGit("rev-parse", "--abbrev-ref", "HEAD").Trim();
        var statusBefore = repo.RunGit("status", "--porcelain");
        var olderSha = repo.RunGit("rev-parse", "HEAD~1").Trim();

        await using var sp = BuildServices();
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var materialiser = CreateMaterialiser(sp);

        try
        {
            // Materialise a commit older than HEAD - if this touched the real worktree, HEAD
            // would move or the working tree would become dirty/detached.
            var result = await materialiser.MaterialiseAsync(repoPath, olderSha);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(result.Path, "src", "App.cs")), Is.EqualTo("class App {}\n"));

            Assert.That(repo.RunGit("rev-parse", "HEAD").Trim(), Is.EqualTo(headBefore));
            Assert.That(repo.RunGit("rev-parse", "--abbrev-ref", "HEAD").Trim(), Is.EqualTo(branchBefore));
            Assert.That(repo.RunGit("status", "--porcelain"), Is.EqualTo(statusBefore));

            // The real worktree file must remain at HEAD's content, not the materialised commit's.
            var workingFile = Path.Combine(repoPath, "src", "App.cs");
            Assert.That(await File.ReadAllTextAsync(workingFile), Is.EqualTo("class App { void Run() {} }\n"));
        }
        finally
        {
            TryDeleteExport(olderSha);
            TryDeleteExport(headBefore);
        }
    }

    /// <summary>Deletes only the export directory for one SHA, mirroring the internal `SanitiseSha`
    /// naming so tests don't clobber unrelated cached exports on the machine running them.</summary>
    private static void TryDeleteExport(string sha)
    {
        var sanitised = new string([.. sha.Where(char.IsLetterOrDigit)]);
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitDelta",
            "trees",
            sanitised);
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}

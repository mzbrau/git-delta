using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Git;
using GitDelta.Review;
using GitDelta.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace GitDelta.Review.Tests;

public sealed class RepositoryCatalogTests
{
    [Test]
    public async Task ScanCatalogAsync_IncludesLinkedWorktreeOutsideScanRoot()
    {
        var parentDir = Path.Combine(Path.GetTempPath(), "gitdelta-catalog-" + Guid.NewGuid().ToString("N"));
        var mainPath = Path.Combine(parentDir, "main-repo");
        var linkedPath = Path.Combine(parentDir, "linked-worktree");
        Directory.CreateDirectory(parentDir);

        var repo = RepositoryBuilder.Create(mainPath).WithFile("README.md", "hello\n").WithInitialCommit();
        repo.Build();
        repo.RunGit("worktree", "add", linkedPath, "-b", "feature/worktree", "main");

        try
        {
            var settings = new TestSettingsStore(new()
            {
                DevelopmentFolder = mainPath,
                RepositoryScanDepth = 1,
            });

            var services = new ServiceCollection();
            services.AddSingleton<ISettingsStore>(settings);
            services.AddGitDeltaGit();
            services.AddSingleton<IRepositoryLocator, RepositoryLocator>();
            await using var provider = services.BuildServiceProvider();

            var locator = provider.GetRequiredService<IRepositoryLocator>();
            var catalog = new List<LocatedRepository>();
            await foreach (var entry in locator.ScanCatalogAsync(ct: CancellationToken.None))
                catalog.Add(entry);

            var linked = catalog.Single(c => c.IsLinkedWorktree && c.LocalPath.EndsWith("linked-worktree", StringComparison.Ordinal));
            Assert.That(linked.MainWorktreePath, Does.EndWith("main-repo"));
            Assert.That(linked.CurrentBranch, Is.EqualTo("feature/worktree"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(linkedPath))
                    repo.RunGit("worktree", "remove", "--force", linkedPath);
            }
            catch
            {
                // Best effort before deleting parent.
            }

            try
            {
                if (Directory.Exists(parentDir))
                    Directory.Delete(parentDir, recursive: true);
            }
            catch
            {
                // Best effort — Windows may deny delete of read-only .git objects.
            }
        }
    }

    [Test]
    public async Task ScanCatalogAsync_IncludesRecentSeedOutsideDevelopmentFolder()
    {
        var parentDir = Path.Combine(Path.GetTempPath(), "gitdelta-seed-" + Guid.NewGuid().ToString("N"));
        var devRoot = Path.Combine(parentDir, "dev");
        var orphanPath = Path.Combine(parentDir, "orphan-repo");
        Directory.CreateDirectory(devRoot);

        RepositoryBuilder.Create(orphanPath).WithFile("README.md", "orphan\n").WithInitialCommit().Build();

        try
        {
            var settings = new TestSettingsStore(new()
            {
                DevelopmentFolder = devRoot,
                RepositoryScanDepth = 2,
            });

            var services = new ServiceCollection();
            services.AddSingleton<ISettingsStore>(settings);
            services.AddGitDeltaGit();
            services.AddSingleton<IRepositoryLocator, RepositoryLocator>();
            await using var provider = services.BuildServiceProvider();

            var locator = provider.GetRequiredService<IRepositoryLocator>();
            var catalog = new List<LocatedRepository>();
            await foreach (var entry in locator.ScanCatalogAsync([orphanPath], CancellationToken.None))
                catalog.Add(entry);

            Assert.That(catalog.Any(c => c.LocalPath.EndsWith("orphan-repo", StringComparison.Ordinal)), Is.True);
        }
        finally
        {
            try
            {
                if (Directory.Exists(parentDir))
                    Directory.Delete(parentDir, recursive: true);
            }
            catch
            {
                // Best effort — Windows may deny delete of read-only .git objects.
            }
        }
    }

    private sealed class TestSettingsStore(AppSettings current) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = current;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Update(Action<AppSettings> mutate) => mutate(Current);
    }
}

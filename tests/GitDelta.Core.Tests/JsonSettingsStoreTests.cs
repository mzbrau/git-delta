using GitDelta.Core;
using GitDelta.Core.Settings;
using NUnit.Framework;

namespace GitDelta.Core.Tests;

public sealed class JsonSettingsStoreTests
{
    [Test]
    public async Task Save_Load_RoundTrip_Persists_Values()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);
            store.Update(s =>
            {
                s.Theme = "Dark";
                s.FontSize = 16;
                s.ContextLines = 10;
                s.IgnoreWhitespace = true;
                s.DefaultDiffMode = DiffViewMode.SideBySide;
                s.SimulateSlowGit = true;
                s.DiffPrefetchConcurrency = 6;
                s.DiffPrefetchDripDelayMs = 100;
                s.DiffPrefetchIndicatorThrottleMs = 200;
                s.DiffPrefetchPriorityPaths = 24;
                s.DiffPrefetchNeighborRadius = 8;
            });
            store.AddRecentRepository("/repo/a");
            store.AddRecentRepository("/repo/b");
            store.Update(s =>
            {
                s.PinnedRepositories = ["/pinned/b", "/pinned/a"];
            });
            await store.SaveAsync();

            var loaded = new JsonSettingsStore(path);
            loaded.Load();
            Assert.That(loaded.Current.Theme, Is.EqualTo("Dark"));
            Assert.That(loaded.Current.FontSize, Is.EqualTo(16));
            Assert.That(loaded.Current.ContextLines, Is.EqualTo(10));
            Assert.That(loaded.Current.IgnoreWhitespace, Is.True);
            Assert.That(loaded.Current.DefaultDiffMode, Is.EqualTo(DiffViewMode.SideBySide));
            Assert.That(loaded.Current.SimulateSlowGit, Is.True);
            Assert.That(loaded.Current.DiffPrefetchConcurrency, Is.EqualTo(6));
            Assert.That(loaded.Current.DiffPrefetchDripDelayMs, Is.EqualTo(100));
            Assert.That(loaded.Current.DiffPrefetchIndicatorThrottleMs, Is.EqualTo(200));
            Assert.That(loaded.Current.DiffPrefetchPriorityPaths, Is.EqualTo(24));
            Assert.That(loaded.Current.DiffPrefetchNeighborRadius, Is.EqualTo(8));
            Assert.That(loaded.Current.RecentRepositories, Is.EqualTo(new[] { "/repo/b", "/repo/a" }));
            Assert.That(loaded.Current.PinnedRepositories, Is.EqualTo(new[] { "/pinned/b", "/pinned/a" }));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void AddRecentRepository_Dedupes_And_Caps_At_20()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);
            for (var i = 0; i < 25; i++)
                store.AddRecentRepository($"/repo/{i}");
            store.AddRecentRepository("/repo/0");

            Assert.That(store.Current.RecentRepositories, Has.Count.EqualTo(20));
            Assert.That(store.Current.RecentRepositories[0], Is.EqualTo("/repo/0"));
            Assert.That(store.Current.RecentRepositories.Count(p => p == "/repo/0"), Is.EqualTo(1));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ToDiffOptions_Maps_Whitespace_And_Context()
    {
        var settings = new AppSettings { ContextLines = 7, IgnoreWhitespace = true, DiffAlgorithm = "myers" };
        var options = settings.ToDiffOptions();
        Assert.That(options.ContextLines, Is.EqualTo(7));
        Assert.That(options.IgnoreAllSpace, Is.True);
        Assert.That(options.Algorithm, Is.EqualTo("myers"));
    }

    [Test]
    public async Task Concurrent_Saves_Persist_Latest_Accounts_Mutation()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);

            // Start a save of an early snapshot, then mutate accounts while it is in flight /
            // queued, then save again. The final file must include the account.
            store.Update(s => s.DevelopmentFolder = "/early");
            var first = store.SaveAsync();

            store.Update(s =>
            {
                s.DevelopmentFolder = "/late";
                s.Accounts.Add(new GitHubAccountSettings
                {
                    Host = "github.com",
                    Login = "octocat",
                    NeedsReauth = false,
                });
            });
            var second = store.SaveAsync();

            await Task.WhenAll(first, second);

            var loaded = new JsonSettingsStore(path);
            loaded.Load();
            Assert.That(loaded.Current.DevelopmentFolder, Is.EqualTo("/late"));
            Assert.That(loaded.Current.Accounts, Has.Count.EqualTo(1));
            Assert.That(loaded.Current.Accounts[0].Login, Is.EqualTo("octocat"));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

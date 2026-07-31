using CodeReviewr.Core;
using CodeReviewr.Core.Settings;
using NUnit.Framework;

namespace CodeReviewr.Core.Tests;

public sealed class JsonSettingsStoreTests
{
    [Test]
    public async Task Save_Load_RoundTrip_Persists_Values()
    {
        var path = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);
            store.Update(s =>
            {
                s.Theme = "Dark";
                s.ContextLines = 10;
                s.IgnoreWhitespace = true;
                s.DefaultDiffMode = DiffViewMode.SideBySide;
            });
            store.AddRecentRepository("/repo/a");
            store.AddRecentRepository("/repo/b");
            await store.SaveAsync();

            var loaded = new JsonSettingsStore(path);
            loaded.Load();
            Assert.That(loaded.Current.Theme, Is.EqualTo("Dark"));
            Assert.That(loaded.Current.ContextLines, Is.EqualTo(10));
            Assert.That(loaded.Current.IgnoreWhitespace, Is.True);
            Assert.That(loaded.Current.DefaultDiffMode, Is.EqualTo(DiffViewMode.SideBySide));
            Assert.That(loaded.Current.RecentRepositories, Is.EqualTo(new[] { "/repo/b", "/repo/a" }));
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
        var path = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"), "settings.json");
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
}

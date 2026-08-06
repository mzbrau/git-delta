using System.Runtime.CompilerServices;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.GitHub;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.Review.Tests;

public sealed class LocalRepositoryLocatorTests
{
    [Test]
    public async Task LocateAsync_Returns_Binding_When_LocalPath_Exists()
    {
        var temp = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var settings = new AppSettings
            {
                RepositoryBindings =
                [
                    new RepositoryAccountBinding
                    {
                        Host = "github.com",
                        Owner = "acme",
                        Name = "demo",
                        LocalPath = temp,
                        AccountLogin = "octocat",
                    },
                ],
            };

            var settingsStore = Substitute.For<ISettingsStore>();
            settingsStore.Current.Returns(settings);

            var locator = new LocalRepositoryLocator(
                new FakeRepositoryLocator(),
                settingsStore,
                Substitute.For<IGitRemoteService>());

            var result = await locator.LocateAsync("github.com", "acme", "demo");

            Assert.That(result.Found, Is.True);
            Assert.That(result.Ambiguous, Is.False);
            Assert.That(result.LocalPath, Is.EqualTo(temp));
            Assert.That(result.Candidates, Is.Null);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task LocateAsync_Reports_Ambiguous_When_Multiple_Matches_And_No_Binding()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.Current.Returns(new AppSettings());

        var locator = new LocalRepositoryLocator(
            new FakeRepositoryLocator(
                new LocatedRepository("/repos/a/demo", "github.com", "acme", "demo", "https://github.com/acme/demo.git"),
                new LocatedRepository("/repos/b/demo", "github.com", "acme", "demo", "https://github.com/acme/demo.git")),
            settingsStore,
            Substitute.For<IGitRemoteService>());

        var result = await locator.LocateAsync("github.com", "acme", "demo");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Ambiguous, Is.True);
        Assert.That(result.LocalPath, Is.Null);
        Assert.That(result.Candidates, Is.EquivalentTo(new[] { "/repos/a/demo", "/repos/b/demo" }));
    }

    [Test]
    public async Task LocateAsync_Single_Scan_Match_Is_Found()
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.Current.Returns(new AppSettings());

        var locator = new LocalRepositoryLocator(
            new FakeRepositoryLocator(
                new LocatedRepository("/repos/only/demo", "github.com", "acme", "demo", "https://github.com/acme/demo.git")),
            settingsStore,
            Substitute.For<IGitRemoteService>());

        var result = await locator.LocateAsync("github.com", "acme", "demo");

        Assert.That(result.Found, Is.True);
        Assert.That(result.Ambiguous, Is.False);
        Assert.That(result.LocalPath, Is.EqualTo("/repos/only/demo"));
    }

    [Test]
    public void BuildCloneUrl_Uses_Github_Or_Enterprise_Host()
    {
        Assert.That(
            LocalRepositoryLocator.BuildCloneUrl("github.com", "acme", "demo"),
            Is.EqualTo("https://github.com/acme/demo.git"));
        Assert.That(
            LocalRepositoryLocator.BuildCloneUrl("github.example.com", "acme", "demo"),
            Is.EqualTo("https://github.example.com/acme/demo.git"));
    }

    private sealed class FakeRepositoryLocator(params LocatedRepository[] matches) : IRepositoryLocator
    {
        public async IAsyncEnumerable<LocatedRepository> ScanAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var match in matches)
            {
                ct.ThrowIfCancellationRequested();
                yield return match;
                await Task.Yield();
            }
        }

        public IAsyncEnumerable<LocatedRepository> ScanLocalAsync(CancellationToken ct = default) =>
            ScanAsync(ct);
    }
}

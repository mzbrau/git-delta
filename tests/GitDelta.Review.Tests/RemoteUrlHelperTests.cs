using GitDelta.Review;
using NUnit.Framework;

namespace GitDelta.Review.Tests;

public sealed class RemoteUrlHelperTests
{
    [TestCase("https://github.com/octocat/Hello-World.git", "github.com", "octocat", "Hello-World")]
    [TestCase("https://github.com/octocat/Hello-World", "github.com", "octocat", "Hello-World")]
    [TestCase("git@github.com:octocat/Hello-World.git", "github.com", "octocat", "Hello-World")]
    [TestCase("ssh://git@github.com/octocat/Hello-World.git", "github.com", "octocat", "Hello-World")]
    [TestCase("https://github.example.com/my-org/my-repo.git", "github.example.com", "my-org", "my-repo")]
    [TestCase("git@github.example.com:my-org/my-repo.git", "github.example.com", "my-org", "my-repo")]
    public void TryParse_ParsesGitHubRemotes(string url, string host, string owner, string name)
    {
        var ok = RemoteUrlHelper.TryParse(url, out var parsedHost, out var parsedOwner, out var parsedName);

        Assert.That(ok, Is.True);
        Assert.That(parsedHost, Is.EqualTo(host));
        Assert.That(parsedOwner, Is.EqualTo(owner));
        Assert.That(parsedName, Is.EqualTo(name));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-remote")]
    [TestCase("https://github.com/onlyone")]
    [TestCase("ftp://github.com/o/r")]
    public void TryParse_RejectsInvalidUrls(string? url)
    {
        Assert.That(RemoteUrlHelper.TryParse(url, out _, out _, out _), Is.False);
    }
}

using GitDelta.App.Services;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class RemoteWebUrlTests
{
    [TestCase("git@github.com:org/repo.git", "https://github.com/org/repo")]
    [TestCase("https://github.com/org/repo.git", "https://github.com/org/repo")]
    [TestCase("http://github.com/org/repo.git", "http://github.com/org/repo")]
    [TestCase("ssh://git@github.com/org/repo.git", "https://github.com/org/repo")]
    [TestCase("git@bitbucket.org:team/repo.git", "https://bitbucket.org/team/repo")]
    [TestCase("https://bitbucket.org/team/repo.git", "https://bitbucket.org/team/repo")]
    [TestCase("ssh://git@bitbucket.org/team/repo.git", "https://bitbucket.org/team/repo")]
    public void ToBrowseUrl_Transforms_Known_Hosts(string input, string expected)
    {
        Assert.That(RemoteWebUrl.ToBrowseUrl(input), Is.EqualTo(expected));
    }

    [Test]
    public void ToBrowseUrl_Converts_Unknown_Scp_Hosts_To_Https()
    {
        Assert.That(RemoteWebUrl.ToBrowseUrl("git@gitlab.example.com:org/repo.git"),
            Is.EqualTo("https://gitlab.example.com/org/repo"));
    }

    [Test]
    public void ToBrowseUrl_Returns_Null_For_Empty_Or_Invalid()
    {
        Assert.That(RemoteWebUrl.ToBrowseUrl(null), Is.Null);
        Assert.That(RemoteWebUrl.ToBrowseUrl(""), Is.Null);
        Assert.That(RemoteWebUrl.ToBrowseUrl("not-a-url"), Is.Null);
    }
}

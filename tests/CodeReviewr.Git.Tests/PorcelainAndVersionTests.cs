using CodeReviewr.Core;
using CodeReviewr.Git;
using NUnit.Framework;

namespace CodeReviewr.Git.Tests;

public sealed class PorcelainStatusParserTests
{
    [Test]
    public void Parses_Ordinary_Modified_Staged_And_Unstaged()
    {
        var input = "1 MM N... 100644 100644 100644 " +
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa " +
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb " +
                    "src/Foo.cs\0";
        var result = PorcelainStatusParser.Parse(input);
        Assert.That(result.Staged, Has.Count.EqualTo(1));
        Assert.That(result.Unstaged, Has.Count.EqualTo(1));
        Assert.That(result.Conflicted, Is.Empty);
        Assert.That(result.Staged[0].Path.Value, Is.EqualTo("src/Foo.cs"));
    }

    [Test]
    public void Parses_Untracked()
    {
        var result = PorcelainStatusParser.Parse("? new.txt\0");
        Assert.That(result.Staged, Is.Empty);
        Assert.That(result.Unstaged, Has.Count.EqualTo(1));
        Assert.That(result.Unstaged[0].Kind, Is.EqualTo(ChangeKind.Untracked));
    }

    [Test]
    public void Parses_Unmerged_As_Conflicted()
    {
        var input = "u UU N... 100644 100644 100644 100644 " +
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa " +
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb " +
                    "cccccccccccccccccccccccccccccccccccccccc " +
                    "conflict.txt\0";
        var result = PorcelainStatusParser.Parse(input);
        Assert.That(result.Conflicted, Has.Count.EqualTo(1));
        Assert.That(result.Conflicted[0].IsConflicted, Is.True);
    }
}

public sealed class GitEnvironmentTests
{
    [Test]
    public async Task DetectAsync_Finds_Git_On_Path()
    {
        var runner = new GitProcessRunner();
        var env = new GitEnvironment(runner, Microsoft.Extensions.Logging.Abstractions.NullLogger<GitEnvironment>.Instance);
        var info = await env.DetectAsync();
        Assert.That(info.Version.MeetsMinimum, Is.True);
        Assert.That(File.Exists(info.Path) || info.Path is "git" or "git.exe", Is.True);
    }
}

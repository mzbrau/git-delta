using GitDelta.Git.Internal;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class WorktreeListParserTests
{
    [Test]
    public void Parse_MainAndLinkedWorktrees()
    {
        const string input = """
            worktree /Users/dev/myapp
            HEAD aaa1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree /Users/dev/myapp-copilot-auth
            HEAD bbb2222222222222222222222222222222222222222
            branch refs/heads/feature/auth

            """;

        var entries = WorktreeListParser.Parse(input);

        var sep = Path.DirectorySeparatorChar;
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries[0].Path, Does.EndWith($"{sep}myapp"));
        Assert.That(entries[0].IsMain, Is.True);
        Assert.That(entries[0].BranchName, Is.EqualTo("main"));
        Assert.That(entries[1].IsMain, Is.False);
        Assert.That(entries[1].BranchName, Is.EqualTo("feature/auth"));
        Assert.That(entries[1].Path, Does.EndWith($"{sep}myapp-copilot-auth"));
    }

    [Test]
    public void Parse_DetachedBareLockedAndPrunable()
    {
        const string input = """
            worktree /tmp/main
            HEAD aaa1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree /tmp/detached
            HEAD bbb2222222222222222222222222222222222222222
            detached

            worktree /tmp/bare
            bare

            worktree /tmp/old
            HEAD ccc3333333333333333333333333333333333333333
            branch refs/heads/old
            locked reason: manual
            prunable gitdir file points to non-existent location

            """;

        var entries = WorktreeListParser.Parse(input);

        Assert.That(entries, Has.Count.EqualTo(4));
        Assert.That(entries[1].IsDetached, Is.True);
        Assert.That(entries[1].BranchName, Is.Null);
        Assert.That(entries[2].IsBare, Is.True);
        Assert.That(entries[3].IsLocked, Is.True);
        Assert.That(entries[3].IsPrunable, Is.True);
    }

    [Test]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        Assert.That(WorktreeListParser.Parse(""), Is.Empty);
        Assert.That(WorktreeListParser.Parse("   \n"), Is.Empty);
    }
}

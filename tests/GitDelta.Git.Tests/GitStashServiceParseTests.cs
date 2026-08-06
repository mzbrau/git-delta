using GitDelta.Core;
using GitDelta.Git;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class GitStashServiceParseTests
{
    [Test]
    public void ParseStashList_Parses_Index_Message_And_Branch()
    {
        var raw = "stash@{0}\0WIP on main: abc1234 message\nstash@{1}\0On feature: older\n";
        var list = GitStashService.ParseStashList(raw);

        Assert.That(list, Has.Count.EqualTo(2));
        Assert.That(list[0].Index, Is.EqualTo(0));
        Assert.That(list[0].Message, Does.Contain("WIP on main"));
        Assert.That(list[0].BranchHint, Is.EqualTo("main"));
        Assert.That(list[0].Ref, Is.EqualTo("stash@{0}"));
        Assert.That(list[1].Index, Is.EqualTo(1));
        Assert.That(list[1].BranchHint, Is.EqualTo("feature"));
    }

    [Test]
    public void ParseNameStatus_Maps_Status_Letters()
    {
        var raw = "M\ta.txt\nA\tb.txt\nD\tc.txt\nR100\told.txt\tnew.txt\n";
        var files = GitStashService.ParseNameStatus(raw);

        Assert.That(files, Has.Count.EqualTo(4));
        Assert.That(files[0], Is.EqualTo((FilePath.From("a.txt"), ChangeKind.Modified)));
        Assert.That(files[1], Is.EqualTo((FilePath.From("b.txt"), ChangeKind.Added)));
        Assert.That(files[2], Is.EqualTo((FilePath.From("c.txt"), ChangeKind.Deleted)));
        Assert.That(files[3], Is.EqualTo((FilePath.From("new.txt"), ChangeKind.Renamed)));
    }
}

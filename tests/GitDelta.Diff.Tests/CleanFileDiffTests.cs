using GitDelta.Core;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using NUnit.Framework;

namespace GitDelta.Diff.Tests;

public sealed class CleanFileDiffTests
{
    [Test]
    public void Create_BuildsContextOnlyHunk()
    {
        var path = FilePath.From("src/App.cs");
        var diff = CleanFileDiff.Create(path, "one\ntwo\n", DiffTarget.HeadToWorktree.AsWorkingCopy());

        Assert.That(diff.IsBinary, Is.False);
        Assert.That(diff.Hunks, Has.Count.EqualTo(1));
        Assert.That(diff.Hunks[0].Lines, Has.Count.EqualTo(2));
        Assert.That(diff.Hunks[0].Lines.All(l => l.Kind == DiffLineKind.Context), Is.True);
        Assert.That(diff.Hunks[0].Lines[0].OldLine, Is.EqualTo(1));
        Assert.That(diff.Hunks[0].Lines[0].NewLine, Is.EqualTo(1));
    }
}

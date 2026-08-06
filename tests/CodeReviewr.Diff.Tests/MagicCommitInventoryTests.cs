using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using NUnit.Framework;

namespace CodeReviewr.Diff.Tests;

public sealed class MagicCommitInventoryTests
{
    [Test]
    public void Build_Treats_Untracked_As_WholeFile_Even_With_Hunks()
    {
        var untracked = UntrackedFileDiff.Create(
            FilePath.From("new.cs"),
            "line1\nline2\n",
            DiffTarget.IndexToWorktree);

        Assert.That(untracked.Hunks, Is.Not.Empty, "Synthetic untracked diffs have hunks");

        var inventory = MagicCommitInventory.Build([untracked]);

        Assert.That(inventory, Has.Count.EqualTo(1));
        Assert.That(inventory[0].WholeFile, Is.True);
        Assert.That(inventory[0].Path, Is.EqualTo("new.cs"));
        Assert.That(inventory[0].Header, Does.Contain("Untracked"));
    }

    [Test]
    public void Build_Keeps_Tracked_Hunks_As_Separate_Items()
    {
        var tracked = PatchParser.Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
             keep
            -x
            +y
            """, DiffTarget.IndexToWorktree);

        var inventory = MagicCommitInventory.Build([tracked]);

        Assert.That(inventory, Has.Count.EqualTo(1));
        Assert.That(inventory[0].WholeFile, Is.False);
    }
}

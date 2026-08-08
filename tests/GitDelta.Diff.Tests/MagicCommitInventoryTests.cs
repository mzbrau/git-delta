using GitDelta.Core;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using NUnit.Framework;

namespace GitDelta.Diff.Tests;

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

    [Test]
    public void Build_Treats_Added_As_WholeFile_Even_With_Hunks()
    {
        var added = PatchParser.Parse(
            """
            diff --git a/new.txt b/new.txt
            new file mode 100644
            index 0000000..e69de29
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,3 @@
            +version https://git-lfs.github.com/spec/v1
            +oid sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789
            +size 1234
            """, DiffTarget.HeadToIndex);

        Assert.That(added.Change, Is.EqualTo(ChangeKind.Added));
        Assert.That(added.Hunks, Is.Not.Empty);

        var inventory = MagicCommitInventory.Build([added]);

        Assert.That(inventory, Has.Count.EqualTo(1));
        Assert.That(inventory[0].WholeFile, Is.True);
        Assert.That(inventory[0].Path, Is.EqualTo("new.txt"));
        Assert.That(inventory[0].Header, Does.Contain("Added"));
    }

    [Test]
    public void Build_Treats_Deleted_As_WholeFile_Even_With_Hunks()
    {
        var deleted = PatchParser.Parse(
            """
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            index e69de29..0000000
            --- a/gone.txt
            +++ /dev/null
            @@ -1,3 +0,0 @@
            -version https://git-lfs.github.com/spec/v1
            -oid sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789
            -size 1234
            """, DiffTarget.HeadToIndex);

        Assert.That(deleted.Change, Is.EqualTo(ChangeKind.Deleted));
        Assert.That(deleted.Hunks, Is.Not.Empty);

        var inventory = MagicCommitInventory.Build([deleted]);

        Assert.That(inventory, Has.Count.EqualTo(1));
        Assert.That(inventory[0].WholeFile, Is.True);
        Assert.That(inventory[0].Path, Is.EqualTo("gone.txt"));
        Assert.That(inventory[0].Header, Does.Contain("Deleted"));
    }
}

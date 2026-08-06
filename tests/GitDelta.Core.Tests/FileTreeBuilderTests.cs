using GitDelta.Core;
using NUnit.Framework;

namespace GitDelta.Core.Tests;

public sealed class FileTreeBuilderTests
{
    [Test]
    public void Build_Separates_Sibling_Prefix_Folders()
    {
        var roots = FileTreeBuilder.Build(
        [
            "src/App.cs",
            "src2/Other.cs",
        ]);

        Assert.That(roots.Count, Is.EqualTo(2));
        Assert.That(roots.Select(r => r.Label), Is.EqualTo(new[] { "src", "src2" }));
        Assert.That(roots[0].Children.Single().FilePath, Is.EqualTo("src/App.cs"));
        Assert.That(roots[1].Children.Single().FilePath, Is.EqualTo("src2/Other.cs"));
    }

    [Test]
    public void Build_Compresses_Single_Child_Folder_Chains()
    {
        var roots = FileTreeBuilder.Build(
        [
            "plugins/gpg/src/main/frontend/keys.js",
            "plugins/gpg/src/main/frontend/table.js",
        ]);

        Assert.That(roots.Count, Is.EqualTo(1));
        Assert.That(roots[0].IsFolder, Is.True);
        Assert.That(roots[0].Label, Is.EqualTo("plugins/gpg/src/main/frontend"));
        Assert.That(roots[0].Children.Count, Is.EqualTo(2));
        Assert.That(roots[0].Children.All(c => !c.IsFolder), Is.True);
    }

    [Test]
    public void Build_Stops_Compression_When_Folder_Has_Files()
    {
        var roots = FileTreeBuilder.Build(
        [
            "src/App.cs",
            "src/util/Helper.cs",
        ]);

        Assert.That(roots.Count, Is.EqualTo(1));
        Assert.That(roots[0].Label, Is.EqualTo("src"));
        Assert.That(roots[0].Children.Count, Is.EqualTo(2));
        Assert.That(roots[0].Children.Any(c => c.Label == "App.cs" && !c.IsFolder), Is.True);
        Assert.That(roots[0].Children.Any(c => c.Label == "util" && c.IsFolder), Is.True);
    }

    [Test]
    public void Build_Root_Level_Files_Have_No_Folder()
    {
        var roots = FileTreeBuilder.Build(["README.md", "LICENSE"]);

        Assert.That(roots.Count, Is.EqualTo(2));
        Assert.That(roots.All(r => !r.IsFolder), Is.True);
        Assert.That(roots.Select(r => r.FilePath), Is.EqualTo(new[] { "LICENSE", "README.md" }));
    }

    [Test]
    public void Flatten_Respects_Collapsed_Folders()
    {
        var roots = FileTreeBuilder.Build(
        [
            "a/b/c.txt",
            "a/d.txt",
        ]);

        var expanded = new List<(FileTreeNode Node, int Depth)>();
        FileTreeBuilder.Flatten(roots, _ => true, expanded);
        Assert.That(expanded.Any(x => x.Node.FilePath == "a/b/c.txt"), Is.True);
        Assert.That(expanded.Any(x => x.Node.FilePath == "a/d.txt"), Is.True);

        var collapsed = new List<(FileTreeNode Node, int Depth)>();
        FileTreeBuilder.Flatten(roots, key => key != "a", collapsed);
        Assert.That(collapsed.Count, Is.EqualTo(1));
        Assert.That(collapsed[0].Node.Label, Is.EqualTo("a"));
        Assert.That(collapsed.Any(x => x.Node.FilePath is not null), Is.False);
    }

    [Test]
    public void Build_Empty_Input_Returns_Empty()
    {
        Assert.That(FileTreeBuilder.Build([]), Is.Empty);
    }
}

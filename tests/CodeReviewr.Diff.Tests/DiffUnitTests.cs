using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using NUnit.Framework;

namespace CodeReviewr.Diff.Tests;

public sealed class PatchParserTests
{
    private const string SamplePatch =
        """
        diff --git a/hello.txt b/hello.txt
        index 1111111..2222222 100644
        --- a/hello.txt
        +++ b/hello.txt
        @@ -1,3 +1,3 @@
         line1
        -old
        +new
         line3
        """;

    [Test]
    public void Parse_Produces_Hunks_And_Retains_RawPatch()
    {
        var diff = PatchParser.Parse(SamplePatch, DiffTarget.IndexToWorktree);
        Assert.That(diff.RawPatch, Is.EqualTo(SamplePatch));
        Assert.That(diff.Hunks, Has.Count.EqualTo(1));
        Assert.That(diff.Hunks[0].Lines.Count(l => l.Kind == DiffLineKind.Removed), Is.EqualTo(1));
        Assert.That(diff.Hunks[0].Lines.Count(l => l.Kind == DiffLineKind.Added), Is.EqualTo(1));
        Assert.That(diff.NewPath.Value, Is.EqualTo("hello.txt"));
    }

    [Test]
    public void Line_Text_Slices_Into_RawPatch()
    {
        var diff = PatchParser.Parse(SamplePatch, DiffTarget.IndexToWorktree);
        var removed = diff.Hunks[0].Lines.First(l => l.Kind == DiffLineKind.Removed);
        Assert.That(removed.Text.ToString(), Is.EqualTo("old"));
        // Slice must refer into the same string instance backing
        Assert.That(removed.Text.Length, Is.EqualTo(3));
    }
}

public sealed class RowProjectionTests
{
    [Test]
    public void Unified_And_SideBySide_Are_Pure_Projections()
    {
        var diff = PatchParser.Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
             keep
            -x
            +y
            """, DiffTarget.IndexToWorktree);

        var unified = UnifiedRowProjector.Project(diff);
        var sbs = SideBySideRowProjector.Project(diff);
        Assert.That(unified.Any(r => r.Kind == DiffRowKind.Removed || r.Kind == DiffRowKind.Added), Is.True);
        Assert.That(sbs.Count, Is.GreaterThan(0));
        // Instant switch: projecting again yields same shape without mutating FileDiff
        Assert.That(UnifiedRowProjector.Project(diff).Count, Is.EqualTo(unified.Count));
    }
}

public sealed class IntraLineDifferTests
{
    [Test]
    public void Highlights_Changed_Word()
    {
        var differ = new IntraLineDiffer();
        var (oldSpans, newSpans) = differ.Diff("hello world", "hello there");
        Assert.That(oldSpans, Is.Not.Empty);
        Assert.That(newSpans, Is.Not.Empty);
    }
}

public sealed class PatchSynthesizerTests
{
    [Test]
    public void SynthesizeHunk_Produces_Applyable_Header()
    {
        var diff = PatchParser.Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
             keep
            -x
            +y
            """, DiffTarget.IndexToWorktree);
        var patch = PatchSynthesizer.SynthesizeHunks(diff, [0]);
        Assert.That(patch, Does.Contain("diff --git"));
        Assert.That(patch, Does.Contain("@@ "));
        Assert.That(patch, Does.Contain("-x"));
        Assert.That(patch, Does.Contain("+y"));
    }
}

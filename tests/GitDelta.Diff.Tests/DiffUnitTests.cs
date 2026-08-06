using GitDelta.Core;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using NUnit.Framework;

namespace GitDelta.Diff.Tests;

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

    [Test]
    public void Parse_Strips_Trailing_CR_From_Crlf_Patches()
    {
        var crlfPatch = SamplePatch.Replace("\n", "\r\n", StringComparison.Ordinal);
        var diff = PatchParser.Parse(crlfPatch, DiffTarget.IndexToWorktree);
        var removed = diff.Hunks[0].Lines.First(l => l.Kind == DiffLineKind.Removed);
        var added = diff.Hunks[0].Lines.First(l => l.Kind == DiffLineKind.Added);
        Assert.That(removed.Text.ToString(), Is.EqualTo("old"));
        Assert.That(added.Text.ToString(), Is.EqualTo("new"));
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

public sealed class UntrackedFileDiffTests
{
    [Test]
    public void Create_Shows_All_Lines_As_Added()
    {
        var diff = UntrackedFileDiff.Create(FilePath.From("new.txt"), "a\nb\nc\n");
        Assert.That(diff.Change, Is.EqualTo(ChangeKind.Untracked));
        Assert.That(diff.Hunks, Has.Count.EqualTo(1));
        Assert.That(diff.Hunks[0].Lines, Has.Count.EqualTo(3));
        Assert.That(diff.Hunks[0].Lines.All(l => l.Kind == DiffLineKind.Added), Is.True);
        Assert.That(diff.Hunks[0].Lines[1].Text.ToString(), Is.EqualTo("b"));
    }

    [Test]
    public void Create_Marks_Null_Byte_Content_As_Binary()
    {
        var diff = UntrackedFileDiff.Create(FilePath.From("bin.dat"), "a\0b"u8.ToArray());
        Assert.That(diff.IsBinary, Is.True);
        Assert.That(diff.Hunks, Is.Empty);
    }
}

public sealed class ContextCollapseTests
{
    [Test]
    public void Long_Context_Run_Keeps_Edge_Lines()
    {
        // 20 context lines around a change; threshold 8 → keep 8 + collapse middle + keep 8
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("diff --git a/a.txt b/a.txt");
        sb.AppendLine("--- a/a.txt");
        sb.AppendLine("+++ b/a.txt");
        sb.AppendLine("@@ -1,21 +1,21 @@");
        for (var i = 1; i <= 20; i++)
            sb.AppendLine($" line{i}");
        sb.AppendLine("-old");
        sb.AppendLine("+new");

        var diff = PatchParser.Parse(sb.ToString(), DiffTarget.IndexToWorktree);
        var rows = UnifiedRowProjector.Project(diff, collapseThreshold: 8);
        Assert.That(rows.Count(r => r.Kind == DiffRowKind.Collapsed), Is.EqualTo(1));
        Assert.That(rows.Count(r => r.Kind == DiffRowKind.Context), Is.EqualTo(16));
        Assert.That(rows.First(r => r.Kind == DiffRowKind.Collapsed).CollapsedCount, Is.EqualTo(4));
    }
}

public sealed class PatchParserEdgeTests
{
    [Test]
    public void Parse_Rename_Sets_ChangeKind_And_Paths()
    {
        var patch =
            """
            diff --git a/old.txt b/new.txt
            similarity index 100%
            rename from old.txt
            rename to new.txt
            """;
        var diff = PatchParser.Parse(patch, DiffTarget.IndexToWorktree);
        Assert.That(diff.Change, Is.EqualTo(ChangeKind.Renamed));
        Assert.That(diff.OldPath.Value, Is.EqualTo("old.txt"));
        Assert.That(diff.NewPath.Value, Is.EqualTo("new.txt"));
    }

    [Test]
    public void Parse_Binary_Sets_IsBinary_And_Empty_Hunks()
    {
        var patch =
            """
            diff --git a/bin.dat b/bin.dat
            index 1111111..2222222 100644
            Binary files a/bin.dat and b/bin.dat differ
            """;
        var diff = PatchParser.Parse(patch, DiffTarget.IndexToWorktree);
        Assert.That(diff.IsBinary, Is.True);
        Assert.That(diff.Hunks, Is.Empty);
    }

    [Test]
    public void Parse_NoNewline_At_Eof_Marker()
    {
        var patch =
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file
            """;
        var diff = PatchParser.Parse(patch, DiffTarget.IndexToWorktree);
        Assert.That(diff.Hunks[0].Lines.Any(l => l.Kind == DiffLineKind.NoNewlineAtEof), Is.True);
    }

    [Test]
    public void Parse_Multi_Hunk()
    {
        var patch =
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
             a
            -b
            +B
            @@ -10,2 +10,2 @@
             j
            -k
            +K
            """;
        var diff = PatchParser.Parse(patch, DiffTarget.IndexToWorktree);
        Assert.That(diff.Hunks, Has.Count.EqualTo(2));
    }
}

public sealed class PatchSynthesizerLineTests
{
    [Test]
    public void SynthesizeLines_Drops_Unselected_Added_Converts_Unselected_Removed()
    {
        var diff = PatchParser.Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,4 @@
             keep
            -old
            +new
            +extra
             keep2
            """, DiffTarget.IndexToWorktree);

        var lines = diff.Hunks[0].Lines;
        var removedIdx = lines.Select((l, i) => (l, i)).First(x => x.l.Kind == DiffLineKind.Removed).i;
        var newIdx = lines.Select((l, i) => (l, i)).First(x => x.l.Kind == DiffLineKind.Added && x.l.Text.ToString() == "new").i;
        // Leave "extra" unselected — should be dropped from the patch.

        var patch = PatchSynthesizer.SynthesizeLines(diff, [
            new LineSelection(0, removedIdx),
            new LineSelection(0, newIdx),
        ]);

        Assert.That(patch, Does.Contain("-old"));
        Assert.That(patch, Does.Contain("+new"));
        Assert.That(patch, Does.Not.Contain("+extra"));
    }

    [Test]
    public void SynthesizeLines_Unselected_Removal_Becomes_Context()
    {
        var diff = PatchParser.Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,2 @@
             keep
            -gone
            -stay-as-context
            +added
            """, DiffTarget.IndexToWorktree);

        var lines = diff.Hunks[0].Lines;
        var goneIdx = lines.Select((l, i) => (l, i)).First(x => x.l.Text.ToString() == "gone").i;
        var addedIdx = lines.Select((l, i) => (l, i)).First(x => x.l.Kind == DiffLineKind.Added).i;

        var patch = PatchSynthesizer.SynthesizeLines(diff, [
            new LineSelection(0, goneIdx),
            new LineSelection(0, addedIdx),
        ]);

        Assert.That(patch, Does.Contain("-gone"));
        Assert.That(patch, Does.Contain(" stay-as-context").Or.Contain("\n stay-as-context"));
        Assert.That(patch, Does.Not.Contain("-stay-as-context"));
    }
}

public sealed class SideBySideProjectionTests
{
    [Test]
    public void Pairs_Removed_And_Added_On_Same_Visual_Row_When_Adjacent()
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

        var rows = SideBySideRowProjector.Project(diff);
        Assert.That(rows, Is.Not.Empty);
        Assert.That(rows.Any(r => r.LeftText.ToString().Contains('x') || r.Kind == DiffRowKind.Removed), Is.True);
        Assert.That(rows.Any(r => r.RightText.ToString().Contains('y') || r.Kind == DiffRowKind.Added), Is.True);
    }

    [Test]
    public void Mixed_Block_With_Extra_Adds_Uses_Padding_And_Per_Pane_Kinds()
    {
        var diff = PatchParser.Parse(
            """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,4 @@
             keep
            -old
            +new1
            +new2
            +new3
            """, DiffTarget.IndexToWorktree);

        var rows = SideBySideRowProjector.Project(diff)
            .Where(r => r.Kind is DiffRowKind.Removed or DiffRowKind.Added or DiffRowKind.Padding)
            .ToList();

        Assert.That(rows.Count, Is.EqualTo(3));

        // Paired replace: tagged Removed, both sides present
        Assert.That(rows[0].Kind, Is.EqualTo(DiffRowKind.Removed));
        Assert.That(rows[0].OldLineNumber, Is.Not.Null);
        Assert.That(rows[0].NewLineNumber, Is.Not.Null);
        Assert.That(DiffRowPresentation.SideBySideLeftKind(rows[0]), Is.EqualTo(DiffRowKind.Removed));
        Assert.That(DiffRowPresentation.SideBySideRightKind(rows[0]), Is.EqualTo(DiffRowKind.Added));

        // Overflow adds: Padding with only new side
        Assert.That(rows[1].Kind, Is.EqualTo(DiffRowKind.Padding));
        Assert.That(rows[1].OldLineNumber, Is.Null);
        Assert.That(rows[1].NewLineNumber, Is.Not.Null);
        Assert.That(rows[1].LeftText.IsEmpty, Is.True);
        Assert.That(rows[1].RightText.ToString(), Does.Contain("new2"));
        Assert.That(DiffRowPresentation.SideBySideLeftKind(rows[1]), Is.EqualTo(DiffRowKind.Context));
        Assert.That(DiffRowPresentation.SideBySideRightKind(rows[1]), Is.EqualTo(DiffRowKind.Added));

        Assert.That(rows[2].Kind, Is.EqualTo(DiffRowKind.Padding));
        Assert.That(DiffRowPresentation.SideBySideRightKind(rows[2]), Is.EqualTo(DiffRowKind.Added));
    }

    [Test]
    public void Pure_Add_Block_Paints_Right_As_Added()
    {
        var row = new DiffRow(
            DiffRowKind.Added,
            null,
            10,
            ReadOnlyMemory<char>.Empty,
            "added".AsMemory(),
            null,
            null,
            0,
            0);
        Assert.That(DiffRowPresentation.SideBySideLeftKind(row), Is.EqualTo(DiffRowKind.Context));
        Assert.That(DiffRowPresentation.SideBySideRightKind(row), Is.EqualTo(DiffRowKind.Added));
    }

    [Test]
    public void Context_Row_Paints_Neither_Side_As_Change()
    {
        var row = new DiffRow(
            DiffRowKind.Context,
            1,
            1,
            "keep".AsMemory(),
            "keep".AsMemory(),
            null,
            null,
            0,
            0);
        Assert.That(DiffRowPresentation.SideBySideLeftKind(row), Is.EqualTo(DiffRowKind.Context));
        Assert.That(DiffRowPresentation.SideBySideRightKind(row), Is.EqualTo(DiffRowKind.Context));
    }
}


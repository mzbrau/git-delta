using GitDelta.App.Controls;
using GitDelta.Core;
using GitDelta.Core.Diff;
using NUnit.Framework;

namespace GitDelta.App.Tests;

[TestFixture]
public sealed class DiffViewerTextSelectionTests
{
    [Test]
    public void CharIndexAtX_ClampsToTextBounds()
    {
        Assert.That(DiffViewerTextSelection.CharIndexAtX(-5, monoCharWidth: 10, textLength: 5), Is.EqualTo(0));
        Assert.That(DiffViewerTextSelection.CharIndexAtX(0, monoCharWidth: 10, textLength: 5), Is.EqualTo(0));
        Assert.That(DiffViewerTextSelection.CharIndexAtX(25, monoCharWidth: 10, textLength: 5), Is.EqualTo(3));
        Assert.That(DiffViewerTextSelection.CharIndexAtX(1000, monoCharWidth: 10, textLength: 5), Is.EqualTo(5));
        Assert.That(DiffViewerTextSelection.CharIndexAtX(10, monoCharWidth: 10, textLength: 0), Is.EqualTo(0));
    }

    [Test]
    public void Normalize_OrdersByRowThenColumn()
    {
        var a = new DiffViewerTextSelection.Anchor(2, 5);
        var b = new DiffViewerTextSelection.Anchor(1, 0);
        var (start, end) = DiffViewerTextSelection.Normalize(a, b);
        Assert.That(start, Is.EqualTo(b));
        Assert.That(end, Is.EqualTo(a));

        var (s2, e2) = DiffViewerTextSelection.Normalize(
            new DiffViewerTextSelection.Anchor(1, 8),
            new DiffViewerTextSelection.Anchor(1, 2));
        Assert.That(s2.Col, Is.EqualTo(2));
        Assert.That(e2.Col, Is.EqualTo(8));
    }

    [Test]
    public void BuildPlainText_MultiLinePartialEnds()
    {
        var lines = new[] { "alpha", "bravo", "charlie" };
        var text = DiffViewerTextSelection.BuildPlainText(
            new DiffViewerTextSelection.Anchor(0, 2),
            new DiffViewerTextSelection.Anchor(2, 4),
            i => lines[i]);

        Assert.That(text, Is.EqualTo("pha\nbravo\nchar"));
    }

    [Test]
    public void BuildPlainText_SkipsNullRows()
    {
        var lines = new[] { "one", null, "three" };
        var text = DiffViewerTextSelection.BuildPlainText(
            new DiffViewerTextSelection.Anchor(0, 0),
            new DiffViewerTextSelection.Anchor(2, 5),
            i => lines[i]);

        Assert.That(text, Is.EqualTo("one\nthree"));
    }

    [Test]
    public void BuildPlainText_Preserves_Blank_First_Line_Separator()
    {
        var lines = new[] { "", "second" };
        var text = DiffViewerTextSelection.BuildPlainText(
            new DiffViewerTextSelection.Anchor(0, 0),
            new DiffViewerTextSelection.Anchor(1, 6),
            i => lines[i]);

        Assert.That(text, Is.EqualTo("\nsecond"));
    }

    [Test]
    public void GetCodeMemory_Unified_ExcludesPrefixConceptually()
    {
        var added = new DiffRow(
            DiffRowKind.Added,
            OldLineNumber: null,
            NewLineNumber: 2,
            LeftText: default,
            RightText: "hello\n".AsMemory(),
            LeftIntraLine: null,
            RightIntraLine: null,
            HunkIndex: 0,
            LineIndexInHunk: 1);

        var mem = DiffViewerTextSelection.GetCodeMemory(added, DiffViewMode.Unified, DiffSide.New);
        Assert.That(mem.ToString(), Is.EqualTo("hello\n"));
    }

    [Test]
    public void GetCodeMemory_SideBySide_UsesSelectedSideOnly()
    {
        var row = new DiffRow(
            DiffRowKind.Context,
            OldLineNumber: 1,
            NewLineNumber: 1,
            LeftText: "left-text".AsMemory(),
            RightText: "right-text".AsMemory(),
            LeftIntraLine: null,
            RightIntraLine: null,
            HunkIndex: 0,
            LineIndexInHunk: 0);

        Assert.That(
            DiffViewerTextSelection.GetCodeMemory(row, DiffViewMode.SideBySide, DiffSide.Old).ToString(),
            Is.EqualTo("left-text"));
        Assert.That(
            DiffViewerTextSelection.GetCodeMemory(row, DiffViewMode.SideBySide, DiffSide.New).ToString(),
            Is.EqualTo("right-text"));
    }

    [Test]
    public void GetLineColumnRange_PartialFirstAndLast()
    {
        var start = new DiffViewerTextSelection.Anchor(1, 2);
        var end = new DiffViewerTextSelection.Anchor(3, 4);

        Assert.That(
            DiffViewerTextSelection.GetLineColumnRange(1, start, end, textLength: 10, isCodeRow: true),
            Is.EqualTo((2, 10)));
        Assert.That(
            DiffViewerTextSelection.GetLineColumnRange(2, start, end, textLength: 7, isCodeRow: true),
            Is.EqualTo((0, 7)));
        Assert.That(
            DiffViewerTextSelection.GetLineColumnRange(3, start, end, textLength: 9, isCodeRow: true),
            Is.EqualTo((0, 4)));
        Assert.That(
            DiffViewerTextSelection.GetLineColumnRange(2, start, end, textLength: 7, isCodeRow: false),
            Is.Null);
    }

    [Test]
    public void IsCodeRow_ExcludesHeadersAndCollapsed()
    {
        Assert.That(DiffViewerTextSelection.IsCodeRow(DiffRowKind.Context), Is.True);
        Assert.That(DiffViewerTextSelection.IsCodeRow(DiffRowKind.Added), Is.True);
        Assert.That(DiffViewerTextSelection.IsCodeRow(DiffRowKind.Removed), Is.True);
        Assert.That(DiffViewerTextSelection.IsCodeRow(DiffRowKind.HunkHeader), Is.False);
        Assert.That(DiffViewerTextSelection.IsCodeRow(DiffRowKind.Collapsed), Is.False);
        Assert.That(DiffViewerTextSelection.IsCodeRow(DiffRowKind.Padding), Is.False);
    }
}

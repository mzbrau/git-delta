using System.Diagnostics;
using GitDelta.App.Controls;
using GitDelta.Core;
using GitDelta.Core.Diff;
using NUnit.Framework;

namespace GitDelta.App.Tests;

[TestFixture]
public sealed class DiffViewerLayoutTests
{
    [Test]
    public void HasCachedHorizontalScroll_NullCache_DoesNotNeedScroll()
    {
        Assert.That(DiffViewerLayout.HasCachedHorizontalScroll(null, viewportCodeWidth: 100), Is.False);
    }

    [Test]
    public void HasCachedHorizontalScroll_CachedWiderThanViewport_NeedsScroll()
    {
        Assert.That(DiffViewerLayout.HasCachedHorizontalScroll(250, viewportCodeWidth: 100), Is.True);
    }

    [Test]
    public void HasCachedHorizontalScroll_CachedWithinViewport_DoesNotNeedScroll()
    {
        Assert.That(DiffViewerLayout.HasCachedHorizontalScroll(80, viewportCodeWidth: 100), Is.False);
    }

    [Test]
    public void ComputeMaxDisplayChars_Unified_IncludesPrefixAndLongestLine()
    {
        var rows = new[]
        {
            new DiffRow(
                DiffRowKind.Context,
                OldLineNumber: 1,
                NewLineNumber: 1,
                LeftText: "short".AsMemory(),
                RightText: "short".AsMemory(),
                LeftIntraLine: null,
                RightIntraLine: null,
                HunkIndex: 0,
                LineIndexInHunk: 0),
            new DiffRow(
                DiffRowKind.Added,
                OldLineNumber: null,
                NewLineNumber: 2,
                LeftText: default,
                RightText: "a-much-longer-added-line\n".AsMemory(),
                LeftIntraLine: null,
                RightIntraLine: null,
                HunkIndex: 0,
                LineIndexInHunk: 1),
        };

        // Unified: 1 (prefix) + trimmed char count of longest line.
        Assert.That(
            DiffViewerLayout.ComputeMaxDisplayChars(rows, DiffViewMode.Unified),
            Is.EqualTo(1 + "a-much-longer-added-line".Length));
    }

    [Test]
    public void ComputeMaxDisplayChars_SideBySide_UsesLongerPaneWithoutPrefix()
    {
        var rows = new[]
        {
            new DiffRow(
                DiffRowKind.Context,
                OldLineNumber: 1,
                NewLineNumber: 1,
                LeftText: "left-side-text".AsMemory(),
                RightText: "right".AsMemory(),
                LeftIntraLine: null,
                RightIntraLine: null,
                HunkIndex: 0,
                LineIndexInHunk: 0),
        };

        Assert.That(
            DiffViewerLayout.ComputeMaxDisplayChars(rows, DiffViewMode.SideBySide),
            Is.EqualTo("left-side-text".Length));
    }

    [Test]
    public void DisplayCharCount_TrimsTrailingNewlines()
    {
        Assert.That(DiffViewerLayout.DisplayCharCount("abc\r\n".AsMemory()), Is.EqualTo(3));
    }

    [Test]
    public void VerticalScrollDeltaPixels_UsesContinuousSensitivity()
    {
        Assert.That(
            DiffViewerLayout.VerticalScrollDeltaPixels(deltaY: 0.25, rowHeight: 20, sensitivity: 1.5),
            Is.EqualTo(7.5).Within(0.001));
        Assert.That(
            DiffViewerLayout.VerticalScrollDeltaPixels(deltaY: 1, rowHeight: 20, sensitivity: 1.5),
            Is.EqualTo(30).Within(0.001));
    }

    [Test]
    public void VerticalScrollDeltaPixels_NearUnitBoundary_IsProportional()
    {
        const double rowHeight = 20;
        var justBelow = DiffViewerLayout.VerticalScrollDeltaPixels(0.99, rowHeight);
        var atUnit = DiffViewerLayout.VerticalScrollDeltaPixels(1.0, rowHeight);
        Assert.That(atUnit / justBelow, Is.EqualTo(1.0 / 0.99).Within(0.001));
        Assert.That(atUnit / justBelow, Is.LessThan(1.5)); // no ×3 cliff
    }

    [Test]
    public void StepScrollLerp_SnapsWhenWithinEpsilon()
    {
        Assert.That(
            DiffViewerLayout.StepScrollLerp(current: 100, target: 100.4, alpha: 0.45, snapPx: 0.5),
            Is.EqualTo(100.4).Within(0.001));
    }

    [Test]
    public void StepScrollLerp_MovesFractionTowardTarget()
    {
        Assert.That(
            DiffViewerLayout.StepScrollLerp(current: 0, target: 100, alpha: 0.45, snapPx: 0.5),
            Is.EqualTo(45).Within(0.001));
    }

    [Test]
    public void IsScrollWarmPaused_WithinIdleWindow_IsTrue()
    {
        var start = Stopwatch.GetTimestamp();
        Assert.That(
            DiffViewerLayout.IsScrollWarmPaused(start, start, idleMs: 100),
            Is.True);
    }

    [Test]
    public void IsScrollWarmPaused_AfterIdleWindow_IsFalse()
    {
        var start = Stopwatch.GetTimestamp();
        // Advance far beyond idle using a synthetic "now" offset in timestamp units.
        var later = start + (long)(Stopwatch.Frequency * 0.25); // 250ms
        Assert.That(
            DiffViewerLayout.IsScrollWarmPaused(start, later, idleMs: 100),
            Is.False);
    }
}

using GitDelta.App.ViewModels;
using GitDelta.Core.Diagnostics;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class DiagnosticsOverlayViewModelTests
{
    [Test]
    public async Task Concurrent_DiffGeneration_Records_Do_Not_Throw()
    {
        var overlay = new DiagnosticsOverlayViewModel();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 200),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) =>
            {
                GitDeltaMeters.DiffGenerationMs.Record(i % 50);
                if (i % 7 == 0)
                    GitDeltaMeters.GitInvocations.Add(1);
                if (i % 11 == 0)
                    GitDeltaMeters.CacheHits.Add(1);
                await Task.Yield();
            });

        // Allow posted UI callbacks / fallback applies to drain.
        await Task.Delay(100);

        Assert.That(overlay.LastTimings.Count, Is.LessThanOrEqualTo(80));
        Assert.That(overlay.GitInvocations, Is.GreaterThan(0));
        Assert.That(overlay.Summary, Does.Contain("git invocations="));
    }

    [Test]
    public void Meter_Callback_Exceptions_Do_Not_Escape_Record()
    {
        // Constructing the overlay attaches a listener; Record must never throw even under burst load.
        _ = new DiagnosticsOverlayViewModel();

        Assert.DoesNotThrow(() =>
        {
            for (var i = 0; i < 500; i++)
                GitDeltaMeters.DiffGenerationMs.Record(1.5);
        });
    }

    [Test]
    public void ShouldListTiming_Excludes_DiffRender()
    {
        Assert.That(
            DiagnosticsOverlayViewModel.ShouldListTiming(DiagnosticsOverlayViewModel.DiffRenderInstrument),
            Is.False);
        Assert.That(
            DiagnosticsOverlayViewModel.ShouldListTiming(DiagnosticsOverlayViewModel.DiffScrollGestureInstrument),
            Is.True);
        Assert.That(
            DiagnosticsOverlayViewModel.ShouldListTiming("diff.generation.duration_ms"),
            Is.True);
    }

    [Test]
    public void IsHighFrequencyTiming_Includes_Render_And_ScrollGesture()
    {
        Assert.That(
            DiagnosticsOverlayViewModel.IsHighFrequencyTiming(DiagnosticsOverlayViewModel.DiffRenderInstrument),
            Is.True);
        Assert.That(
            DiagnosticsOverlayViewModel.IsHighFrequencyTiming(DiagnosticsOverlayViewModel.DiffScrollGestureInstrument),
            Is.True);
        Assert.That(
            DiagnosticsOverlayViewModel.IsHighFrequencyTiming("diff.generation.duration_ms"),
            Is.False);
    }

    [Test]
    public async Task Scroll_Gesture_Sample_Survives_DiffRender_Flood()
    {
        var overlay = new DiagnosticsOverlayViewModel();

        GitDeltaMeters.DiffScrollGestureToPaintMs.Record(12.5);
        for (var i = 0; i < 100; i++)
            GitDeltaMeters.DiffRenderMs.Record(1.0 + (i % 5));

        await Task.Delay(100);

        Assert.That(overlay.LastScrollGestureMs, Is.EqualTo(12.5).Within(0.01));
        Assert.That(overlay.LastDiffRenderMs, Is.Not.Null);
        Assert.That(overlay.Summary, Does.Contain("last scroll gesture=12.5 ms"));
        Assert.That(
            overlay.LastTimings.Any(line =>
                line.StartsWith(DiagnosticsOverlayViewModel.DiffScrollGestureInstrument, StringComparison.Ordinal)),
            Is.True);
        Assert.That(
            overlay.LastTimings.Any(line =>
                line.StartsWith(DiagnosticsOverlayViewModel.DiffRenderInstrument, StringComparison.Ordinal)),
            Is.False);
    }
}

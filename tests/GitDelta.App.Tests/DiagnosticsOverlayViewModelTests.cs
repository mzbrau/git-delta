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

        Assert.That(overlay.LastTimings.Count, Is.LessThanOrEqualTo(50));
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
}

using CodeReviewr.Core;
using CodeReviewr.Core.AI;
using NUnit.Framework;

namespace CodeReviewr.AI.Tests;

public sealed class FileBriefingEligibilityTests
{
    [Test]
    public void IsEligible_BelowMinLinesChanged_ReturnsFalse()
    {
        var eligible = FileBriefingEligibility.IsEligible(
            changePercent: 100,
            linesAdded: 2,
            linesRemoved: 3,
            minChangePercent: 25,
            minLinesChanged: 10);

        Assert.That(eligible, Is.False);
    }

    [Test]
    public void IsEligible_UnknownChangePercent_AndLinesMeetThreshold_ReturnsTrue()
    {
        var eligible = FileBriefingEligibility.IsEligible(
            changePercent: null,
            linesAdded: 6,
            linesRemoved: 5,
            minChangePercent: 25,
            minLinesChanged: 10);

        Assert.That(eligible, Is.True);
    }

    [Test]
    public void IsEligible_ChangePercentBelowThreshold_ReturnsFalse()
    {
        var eligible = FileBriefingEligibility.IsEligible(
            changePercent: 10,
            linesAdded: 6,
            linesRemoved: 5,
            minChangePercent: 25,
            minLinesChanged: 10);

        Assert.That(eligible, Is.False);
    }

    [Test]
    public void IsEligible_ChangePercentMeetsThreshold_AndLinesMeetThreshold_ReturnsTrue()
    {
        var eligible = FileBriefingEligibility.IsEligible(
            changePercent: 25,
            linesAdded: 6,
            linesRemoved: 5,
            minChangePercent: 25,
            minLinesChanged: 10);

        Assert.That(eligible, Is.True);
    }

    [Test]
    public void IsEligible_AiChangedFileFactOverload_UsesSettingsThresholds()
    {
        var settings = new AppSettings
        {
            AiFileBriefingMinChangePercent = 50,
            AiFileBriefingMinLinesChanged = 5,
        };

        var eligibleFile = new AiChangedFileFact(
            "src/Foo.cs", "Modified", "before", "after", LinesAdded: 4, LinesRemoved: 4, ChangePercent: 60);
        var ineligibleFile = new AiChangedFileFact(
            "src/Bar.cs", "Modified", "before", "after", LinesAdded: 1, LinesRemoved: 1, ChangePercent: 90);

        Assert.That(FileBriefingEligibility.IsEligible(eligibleFile, settings), Is.True);
        Assert.That(FileBriefingEligibility.IsEligible(ineligibleFile, settings), Is.False);
    }
}

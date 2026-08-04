using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using NUnit.Framework;

namespace CodeReviewr.Core.Tests;

public sealed class FileChangeStatsTests
{
    [Test]
    public void FromCounts_WithContext_ComputesPercent()
    {
        // 60 context + 20 added + 20 removed => 40% changed
        var stats = FileChangeStats.FromCounts(20, 20, 60, ChangeKind.Modified);
        Assert.That(stats.LinesAdded, Is.EqualTo(20));
        Assert.That(stats.LinesRemoved, Is.EqualTo(20));
        Assert.That(stats.ChangePercent, Is.EqualTo(40));
    }

    [Test]
    public void FromCounts_AddedFile_Is100Percent()
    {
        var stats = FileChangeStats.FromCounts(40, 0, totalLines: 40, ChangeKind.Added);
        Assert.That(stats.ChangePercent, Is.EqualTo(100));
    }

    [Test]
    public void FromCounts_ModifiedWithoutTotal_HasNullPercent()
    {
        var stats = FileChangeStats.FromCounts(5, 3, totalLines: null, ChangeKind.Modified);
        Assert.That(stats.LinesAdded, Is.EqualTo(5));
        Assert.That(stats.LinesRemoved, Is.EqualTo(3));
        Assert.That(stats.ChangePercent, Is.Null);
    }
}

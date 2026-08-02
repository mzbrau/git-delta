using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.Review;
using NUnit.Framework;

namespace CodeReviewr.Review.Tests;

public sealed class AnchorMigratorTests
{
    [Test]
    public async Task Migrate_InlineComment_MapsLineAndMarksOutdated()
    {
        var source = new[] { "alpha", "beta", "gamma", "delta" };
        var target = new[] { "alpha", "insert", "beta", "gamma", "delta" };

        var thread = new ReviewThread(
            "T1", "f.txt", 2, null, false, true,
            [],
            Side: DiffSide.New,
            CommitOid: "old",
            OriginalCommitOid: "old",
            OriginalLine: 2,
            DiffHunk: "@@ never render");

        var migrated = AnchorMigrator.Migrate(
            thread,
            DiffSide.New,
            ContentId.FromSha("newblob"),
            source,
            target,
            2,
            null);

        await Verify(new
        {
            migrated.IsOutdated,
            migrated.IsUnplaceable,
            migrated.Line,
            Side = migrated.Anchor?.Start.Side,
            StartLine = migrated.Anchor?.Start.Line,
            EndLine = migrated.Anchor?.End.Line,
            migrated.ContextLines,
        });
    }

    [Test]
    public async Task Migrate_DeletedLine_BecomesUnplaceableWithLocalContext()
    {
        var source = new[] { "keep", "remove-me", "tail" };
        var target = new[] { "keep", "tail" };

        var thread = new ReviewThread(
            "T2", "f.txt", 2, null, false, true,
            [],
            Side: DiffSide.New,
            CommitOid: "old",
            OriginalCommitOid: "old",
            OriginalLine: 2,
            DiffHunk: "@@ never render");

        var migrated = AnchorMigrator.Migrate(
            thread,
            DiffSide.New,
            ContentId.FromSha("newblob"),
            source,
            target,
            2,
            null);

        await Verify(new
        {
            migrated.IsUnplaceable,
            migrated.Anchor,
            migrated.ContextLines,
            BodySample = migrated.Comments.Count > 0 ? migrated.Comments[0].Body : null,
        });

        Assert.That(migrated.ContextLines, Does.Not.Contain("never render"));
    }

    [Test]
    public void LineMapper_MapsStableLinesThroughInsertions()
    {
        var oldLines = new[] { "a", "b", "c" };
        var newLines = new[] { "a", "insert", "b", "c" };

        var mapped = LineMapper.TryMapLine(2, oldLines, newLines);
        Assert.That(mapped, Is.EqualTo(3));
    }
}

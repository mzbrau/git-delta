using System.Text.Json;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.Review;
using NUnit.Framework;

namespace CodeReviewr.Review.Tests;

public sealed class ReviewThreadParserTests
{
    [Test]
    public void ParseReviewThreads_FromFixture_MapsFieldsAndNeverSurfacesDiffHunkInComments()
    {
        var json = File.ReadAllText(Path.Combine("Fixtures", "pull-request-threads-response.json"));
        using var doc = JsonDocument.Parse(json);
        var threads = ReviewThreadParser.Parse(doc.RootElement);

        Assert.That(threads, Has.Count.EqualTo(2));

        var right = threads[0];
        Assert.That(right.NodeId, Is.EqualTo("RT_kwDO"));
        Assert.That(right.Path, Is.EqualTo("src/Foo.cs"));
        Assert.That(right.Side, Is.EqualTo(DiffSide.New));
        Assert.That(right.Line, Is.EqualTo(12));
        Assert.That(right.DiffHunk, Does.Contain("should-never-render"));
        Assert.That(right.Comments[0].Body, Does.Not.Contain("should-never-render"));
        Assert.That(right.Comments[0].AuthorLogin, Is.EqualTo("reviewer"));

        var left = threads[1];
        Assert.That(left.Side, Is.EqualTo(DiffSide.Old));
        Assert.That(left.IsOutdated, Is.True);
        Assert.That(left.IsResolved, Is.True);
        Assert.That(left.StartLine, Is.EqualTo(3));
        Assert.That(left.Comments[0].ViewerDidAuthor, Is.True);
    }
}

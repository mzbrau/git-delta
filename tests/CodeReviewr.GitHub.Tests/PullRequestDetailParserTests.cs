using System.Text.Json;
using CodeReviewr.GitHub;
using NUnit.Framework;

namespace CodeReviewr.GitHub.Tests;

public sealed class PullRequestDetailParserTests
{
    [Test]
    public void ParseDetail_Includes_Context_Fields()
    {
        var json = File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "pull-request-detail-response.json"));
        using var doc = JsonDocument.Parse(json);
        var pr = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");

        var detail = PullRequestGraphQLParser.ParseDetail(
            pr,
            "github.com",
            "dev",
            InboxSection.NeedsMyReview);

        Assert.That(detail.Body, Does.Contain("Summary"));
        Assert.That(detail.Mergeable, Is.True);
        Assert.That(detail.MergeStateStatus, Is.EqualTo("CLEAN"));
        Assert.That(detail.CheckRollupState, Is.EqualTo("SUCCESS"));
        Assert.That(detail.StatusChecks, Has.Count.EqualTo(1));
        Assert.That(detail.StatusChecks![0].Name, Is.EqualTo("build"));
        Assert.That(detail.Timeline, Has.Count.EqualTo(2));
        Assert.That(detail.Timeline!.Any(t => t.Kind == "comment"), Is.True);
        Assert.That(detail.Timeline.Any(t => t.Kind == "review"), Is.True);
    }
}

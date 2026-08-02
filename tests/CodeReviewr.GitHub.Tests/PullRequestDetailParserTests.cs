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

    [Test]
    public void ParseDetail_Pending_Review_Uses_CreatedAt_When_SubmittedAt_Null()
    {
        const string json = """
            {
              "id": "PR_pending",
              "number": 7,
              "title": "WIP",
              "url": "https://github.com/octo/repo/pull/7",
              "isDraft": false,
              "createdAt": "2026-07-01T08:00:00Z",
              "updatedAt": "2026-07-31T12:00:00Z",
              "reviewDecision": null,
              "mergeable": true,
              "mergeStateStatus": "CLEAN",
              "baseRefName": "main",
              "headRefName": "feature",
              "changedFiles": 0,
              "baseRefOid": "abc",
              "headRefOid": "def",
              "body": "desc",
              "author": { "login": "octocat" },
              "repository": {
                "id": "R_1",
                "name": "repo",
                "nameWithOwner": "octo/repo",
                "owner": { "login": "octo" },
                "url": "https://github.com/octo/repo"
              },
              "comments": { "nodes": [] },
              "reviews": {
                "nodes": [
                  {
                    "author": { "login": "mzbrau" },
                    "body": "pending note",
                    "state": "PENDING",
                    "submittedAt": null,
                    "createdAt": "2026-07-31T16:30:00Z",
                    "url": "https://github.com/octo/repo/pull/7#pullrequestreview-9"
                  }
                ]
              },
              "commits": { "nodes": [] },
              "files": { "nodes": [] }
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var detail = PullRequestGraphQLParser.ParseDetail(
            doc.RootElement,
            "github.com",
            "mzbrau",
            InboxSection.MyPullRequests);

        Assert.That(detail.Timeline, Has.Count.EqualTo(1));
        var entry = detail.Timeline![0];
        Assert.That(entry.Kind, Is.EqualTo("review"));
        Assert.That(entry.ReviewState, Is.EqualTo("PENDING"));
        Assert.That(entry.AuthorLogin, Is.EqualTo("mzbrau"));
        Assert.That(entry.CreatedAt, Is.EqualTo(DateTimeOffset.Parse("2026-07-31T16:30:00Z")));
        Assert.That(entry.CreatedAt, Is.Not.EqualTo(DateTimeOffset.MinValue));
    }
}

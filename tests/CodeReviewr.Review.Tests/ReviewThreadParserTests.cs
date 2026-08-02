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
        var json = File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "pull-request-threads-response.json"));
        using var doc = JsonDocument.Parse(json);
        var threads = ReviewThreadParser.Parse(doc.RootElement);

        Assert.That(threads, Has.Count.EqualTo(2));

        var right = threads[0];
        Assert.That(right.NodeId, Is.EqualTo("RT_kwDO"));
        Assert.That(right.Path, Is.EqualTo("src/Foo.cs"));
        Assert.That(right.Side, Is.EqualTo(DiffSide.New));
        Assert.That(right.Line, Is.EqualTo(12));
        Assert.That(right.SubjectType, Is.EqualTo(ReviewThreadSubjectType.Line));
        Assert.That(right.IsFileLevel, Is.False);
        Assert.That(right.DiffHunk, Does.Contain("should-never-render"));
        Assert.That(right.CommitOid, Is.EqualTo("abc123def456789012345678901234567890abcd"));
        Assert.That(right.OriginalCommitOid, Is.EqualTo("abc123def456789012345678901234567890abcd"));
        Assert.That(right.Comments[0].Body, Does.Not.Contain("should-never-render"));
        Assert.That(right.Comments[0].AuthorLogin, Is.EqualTo("reviewer"));

        var left = threads[1];
        Assert.That(left.Side, Is.EqualTo(DiffSide.Old));
        Assert.That(left.IsOutdated, Is.True);
        Assert.That(left.IsResolved, Is.True);
        Assert.That(left.StartLine, Is.EqualTo(3));
        Assert.That(left.CommitOid, Is.EqualTo("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"));
        Assert.That(left.OriginalCommitOid, Is.EqualTo("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"));
        Assert.That(left.Comments[0].ViewerDidAuthor, Is.True);
    }

    [Test]
    public void ParseReviewThreads_FileSubjectType_MapsFileLevel()
    {
        const string json = """
            {
              "repository": {
                "pullRequest": {
                  "reviewThreads": {
                    "nodes": [
                      {
                        "id": "RT_file",
                        "isResolved": false,
                        "isOutdated": false,
                        "path": "src/Foo.cs",
                        "line": null,
                        "startLine": null,
                        "diffSide": null,
                        "subjectType": "FILE",
                        "originalLine": null,
                        "originalStartLine": null,
                        "comments": {
                          "nodes": [
                            {
                              "id": "RC_file",
                              "body": "File-level note",
                              "createdAt": "2026-01-15T10:30:00Z",
                              "url": null,
                              "viewerDidAuthor": false,
                              "author": { "login": "reviewer" },
                              "diffHunk": null,
                              "commit": null,
                              "originalCommit": null
                            }
                          ]
                        }
                      }
                    ]
                  }
                }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var threads = ReviewThreadParser.Parse(doc.RootElement);

        Assert.That(threads, Has.Count.EqualTo(1));
        Assert.That(threads[0].SubjectType, Is.EqualTo(ReviewThreadSubjectType.File));
        Assert.That(threads[0].IsFileLevel, Is.True);
        Assert.That(threads[0].Side, Is.Null);
        Assert.That(threads[0].Line, Is.Null);
    }
}

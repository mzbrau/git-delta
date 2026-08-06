using GitDelta.AI;
using NUnit.Framework;

namespace GitDelta.AI.Tests;

public sealed class MagicCommitPlanParserTests
{
    [Test]
    public void Parses_Preferred_Message_And_HunkIds_Shape()
    {
        const string json = """
            {
              "commits": [
                {
                  "message": "Trim README intro copy\n\nKeep the opening concise.",
                  "hunkIds": ["h1"]
                },
                {
                  "message": "Add Magic Commit workflow",
                  "hunkIds": ["h2", "h3"]
                }
              ]
            }
            """;

        var plan = MagicCommitPlanParser.Parse(json);

        Assert.That(plan.Commits, Has.Count.EqualTo(2));
        Assert.That(plan.Commits[0].Message, Is.EqualTo("Trim README intro copy\n\nKeep the opening concise."));
        Assert.That(plan.Commits[0].HunkIds, Is.EqualTo(new[] { "h1" }));
        Assert.That(plan.Commits[1].Message, Is.EqualTo("Add Magic Commit workflow"));
        Assert.That(plan.Commits[1].HunkIds, Is.EqualTo(new[] { "h2", "h3" }));
    }

    [Test]
    public void Parses_Subject_Body_And_SnakeCase_HunkIds()
    {
        // Shape Copilot actually emitted when no JSON schema was available.
        const string json = """
            {
              "commits": [
                {
                  "subject": "Trim README intro copy",
                  "body": "Keep the opening concise and remove the outdated phase-specific sentence.",
                  "hunk_ids": ["h1"]
                },
                {
                  "subject": "Add AI commit assist and Magic Commit workflow",
                  "body": "Wire AI prompt/catalog and DI support.",
                  "hunk_ids": ["h2", "h3", "h4"]
                }
              ]
            }
            """;

        var plan = MagicCommitPlanParser.Parse(json);

        Assert.That(plan.Commits, Has.Count.EqualTo(2));
        Assert.That(plan.Commits[0].Message, Is.EqualTo(
            "Trim README intro copy\n\nKeep the opening concise and remove the outdated phase-specific sentence."));
        Assert.That(plan.Commits[0].HunkIds, Is.EqualTo(new[] { "h1" }));
        Assert.That(plan.Commits[1].Message, Is.EqualTo(
            "Add AI commit assist and Magic Commit workflow\n\nWire AI prompt/catalog and DI support."));
        Assert.That(plan.Commits[1].HunkIds, Is.EqualTo(new[] { "h2", "h3", "h4" }));
    }

    [Test]
    public void Parses_Subject_Only_Without_Body()
    {
        const string json = """
            {"commits":[{"subject":"Fix typo","hunk_ids":["h1"]}]}
            """;

        var plan = MagicCommitPlanParser.Parse(json);

        Assert.That(plan.Commits, Has.Count.EqualTo(1));
        Assert.That(plan.Commits[0].Message, Is.EqualTo("Fix typo"));
        Assert.That(plan.Commits[0].HunkIds, Is.EqualTo(new[] { "h1" }));
    }

    [Test]
    public void Prefers_Message_Over_Subject_When_Both_Present()
    {
        const string json = """
            {"commits":[{"message":"Use this","subject":"Ignore","hunkIds":["h1"]}]}
            """;

        var plan = MagicCommitPlanParser.Parse(json);

        Assert.That(plan.Commits[0].Message, Is.EqualTo("Use this"));
    }

    [Test]
    public void Rejects_Empty_Message()
    {
        const string json = """
            {"commits":[{"message":"","hunkIds":["h1"]}]}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => MagicCommitPlanParser.Parse(json));
        Assert.That(ex!.Message, Does.Contain("missing a message"));
    }

    [Test]
    public void Rejects_Missing_HunkIds()
    {
        const string json = """
            {"commits":[{"message":"Do something","hunkIds":[]}]}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => MagicCommitPlanParser.Parse(json));
        Assert.That(ex!.Message, Does.Contain("no hunk IDs"));
    }

    [Test]
    public void Rejects_Empty_Commits_Array()
    {
        const string json = """{"commits":[]}""";

        var ex = Assert.Throws<InvalidOperationException>(() => MagicCommitPlanParser.Parse(json));
        Assert.That(ex!.Message, Does.Contain("no commits"));
    }

    [Test]
    public void Parses_Bare_Commits_Array()
    {
        const string json = """
            [{"message":"One","hunkIds":["h1"]}]
            """;

        var plan = MagicCommitPlanParser.Parse(json);

        Assert.That(plan.Commits, Has.Count.EqualTo(1));
        Assert.That(plan.Commits[0].Message, Is.EqualTo("One"));
    }
}

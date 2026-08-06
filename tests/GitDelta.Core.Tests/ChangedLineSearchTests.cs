using GitDelta.Core;
using GitDelta.Core.Diff;
using NUnit.Framework;

namespace GitDelta.Core.Tests;

public sealed class ChangedLineSearchTests
{
    [Test]
    public void FindHits_MatchesAddedAndRemoved_IgnoresContext()
    {
        var diff = MakeDiff(
            new DiffLine(DiffLineKind.Context, 1, 1, "keep dog here".AsMemory()),
            new DiffLine(DiffLineKind.Removed, 2, null, "old dog house".AsMemory()),
            new DiffLine(DiffLineKind.Added, null, 2, "new cat house".AsMemory()),
            new DiffLine(DiffLineKind.Added, null, 3, "another Dog park".AsMemory()));

        var hits = ChangedLineSearch.FindHits(diff, "dog");

        Assert.That(hits, Has.Count.EqualTo(2));
        Assert.That(hits[0].Side, Is.EqualTo(DiffSide.Old));
        Assert.That(hits[0].LineNumber, Is.EqualTo(2));
        Assert.That(hits[1].Side, Is.EqualTo(DiffSide.New));
        Assert.That(hits[1].LineNumber, Is.EqualTo(3));
    }

    [Test]
    public void FindHits_BinaryOrEmptyQuery_ReturnsEmpty()
    {
        var diff = MakeDiff(
            isBinary: true,
            new DiffLine(DiffLineKind.Added, null, 1, "dog".AsMemory()));

        Assert.That(ChangedLineSearch.FindHits(diff, "dog"), Is.Empty);
        Assert.That(ChangedLineSearch.FindHits(MakeDiff(
            new DiffLine(DiffLineKind.Added, null, 1, "dog".AsMemory())), "  "), Is.Empty);
    }

    [Test]
    public void FormatSnippet_IncludesRadiusAroundMatch_TruncatesRest()
    {
        var prefix = new string('a', 50);
        var suffix = new string('b', 50);
        var line = prefix + "MATCH" + suffix;

        var snippet = ChangedLineSearch.FormatSnippet(line, prefix.Length, 5, radius: 40);

        Assert.That(snippet, Does.StartWith("…"));
        Assert.That(snippet, Does.EndWith("…"));
        Assert.That(snippet, Does.Contain("MATCH"));
        Assert.That(snippet.Length, Is.EqualTo(1 + 40 + 5 + 40 + 1));
    }

    [Test]
    public void FormatSnippet_NoTruncationWhenShort()
    {
        var snippet = ChangedLineSearch.FormatSnippet("hello dog world", 6, 3, radius: 40);
        Assert.That(snippet, Is.EqualTo("hello dog world"));
    }

    [Test]
    public void FormatSnippetParts_ReportsMatchOffsetWithinSnippet()
    {
        var parts = ChangedLineSearch.FormatSnippetParts("hello dog world", 6, 3, radius: 40);

        Assert.That(parts.Text, Is.EqualTo("hello dog world"));
        Assert.That(parts.MatchIndex, Is.EqualTo(6));
        Assert.That(parts.MatchLength, Is.EqualTo(3));
        Assert.That(parts.Text.Substring(parts.MatchIndex, parts.MatchLength), Is.EqualTo("dog"));
    }

    [Test]
    public void FormatSnippetParts_AccountsForEllipsisAndTrim()
    {
        var indent = new string(' ', 10);
        var prefix = new string('a', 50);
        var suffix = new string('b', 50);
        var line = indent + prefix + "MATCH" + suffix;

        var parts = ChangedLineSearch.FormatSnippetParts(line, indent.Length + prefix.Length, 5, radius: 40);

        Assert.That(parts.Text, Does.StartWith("…"));
        Assert.That(parts.Text.Substring(parts.MatchIndex, parts.MatchLength), Is.EqualTo("MATCH"));
    }

    [Test]
    public void FormatSnippet_StripsLeadingWhitespace()
    {
        var indent = new string(' ', 20);
        var line = indent + "return dog.Count;";
        var matchIndex = indent.Length + "return ".Length;

        var snippet = ChangedLineSearch.FormatSnippet(line, matchIndex, 3, radius: 40);

        Assert.That(snippet, Is.EqualTo("return dog.Count;"));
        Assert.That(snippet, Does.Not.StartWith(" "));
    }

    [Test]
    public void FindHits_SnippetStripsLeadingWhitespace()
    {
        var indent = new string('\t', 3);
        var diff = MakeDiff(
            new DiffLine(DiffLineKind.Added, null, 1, (indent + "foo dog bar").AsMemory()));

        var hits = ChangedLineSearch.FindHits(diff, "dog");

        Assert.That(hits, Has.Count.EqualTo(1));
        Assert.That(hits[0].Snippet, Is.EqualTo("foo dog bar"));
        Assert.That(hits[0].SnippetMatchIndex, Is.EqualTo(4));
        Assert.That(hits[0].SnippetMatchLength, Is.EqualTo(3));
    }

    private static FileDiff MakeDiff(params DiffLine[] lines) => MakeDiff(isBinary: false, lines);

    private static FileDiff MakeDiff(bool isBinary, params DiffLine[] lines)
    {
        var hunk = new DiffHunk(1, lines.Length, 1, lines.Length, "@@", lines);
        return new FileDiff(
            DiffTarget.IndexToWorktree.AsWorkingCopy(),
            FilePath.From("a.txt"),
            FilePath.From("a.txt"),
            ChangeKind.Modified,
            ContentId.Empty,
            ContentId.Empty,
            isBinary,
            [hunk],
            "");
    }
}

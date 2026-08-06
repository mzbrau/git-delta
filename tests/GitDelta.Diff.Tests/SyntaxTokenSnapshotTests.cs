using GitDelta.Core;
using GitDelta.Diff;
using NUnit.Framework;

namespace GitDelta.Diff.Tests;

public sealed class SyntaxTokenSnapshotTests
{
    [Test]
    public async Task Tokenises_CSharp_Fixture_Matches_Snapshot()
    {
        var source =
            """
            using System;

            namespace Demo;

            public sealed class Sample
            {
                // greeting
                public string Hello(int n) => $"hi {n}";
            }
            """;

        var service = new SyntaxTokenService();
        var content = ContentId.FromSha(new string('a', 40));
        var tokens = await service.TokeniseAsync(content, FilePath.From("Sample.cs"), source);

        Assert.That(tokens, Is.Not.Null);
        await Verify(FormatTokens(tokens!));
    }

    [Test]
    public async Task Second_Tokenise_Of_Same_Content_Does_Not_Retokenise_Lines()
    {
        var source = "public class A { }\n";
        var service = new SyntaxTokenService();
        var content = ContentId.FromSha(new string('b', 40));

        var first = await service.TokeniseAsync(content, FilePath.From("A.cs"), source);
        Assert.That(first, Is.Not.Null);

        // Cache hit path: same ContentId must return the same instance without re-tokenising.
        var second = await service.TokeniseAsync(content, FilePath.From("A.cs"), source);
        Assert.That(second, Is.SameAs(first));
    }

    private static string FormatTokens(FileSyntaxTokens tokens)
    {
        var lines = new List<string> { $"grammar={tokens.GrammarScope}" };
        foreach (var line in tokens.Lines)
        {
            var spans = string.Join(
                ',',
                line.Spans.Select(s => $"{s.Start}+{s.Length}:{TrimScope(s.Scope)}"));
            lines.Add($"{line.LineNumber}:{spans}");
        }

        return string.Join('\n', lines);
    }

    private static string TrimScope(string scope)
    {
        // Keep the leaf scope segment for stable, readable snapshots.
        var idx = scope.LastIndexOf('.');
        return idx < 0 ? scope : scope[(idx + 1)..];
    }
}

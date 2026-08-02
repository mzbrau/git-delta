using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using NUnit.Framework;

namespace CodeReviewr.Diff.Tests;

public sealed class RowProjectionSnapshotTests
{
    private const string SamplePatch =
        """
        diff --git a/Sample.cs b/Sample.cs
        index 1111111..2222222 100644
        --- a/Sample.cs
        +++ b/Sample.cs
        @@ -1,4 +1,4 @@
         using System;
        -public class Foo { }
        +public class Bar { }
         // trailing
        """;

    [Test]
    public async Task Unified_Projection_With_IntraLine_Matches_Snapshot()
    {
        var diff = IntraLineEnricher.Enrich(
            PatchParser.Parse(SamplePatch, DiffTarget.IndexToWorktree),
            new IntraLineDiffer());
        var rows = UnifiedRowProjector.Project(diff);
        await Verify(FormatRows(rows));
    }

    [Test]
    public async Task SideBySide_Projection_With_IntraLine_Matches_Snapshot()
    {
        var diff = IntraLineEnricher.Enrich(
            PatchParser.Parse(SamplePatch, DiffTarget.IndexToWorktree),
            new IntraLineDiffer());
        var rows = SideBySideRowProjector.Project(diff);
        await Verify(FormatRows(rows));
    }

    private static string FormatRows(IReadOnlyList<DiffRow> rows)
    {
        var lines = new List<string>(rows.Count);
        foreach (var r in rows)
        {
            lines.Add(
                $"{r.Kind}|old={r.OldLineNumber?.ToString() ?? "-"}|new={r.NewLineNumber?.ToString() ?? "-"}|" +
                $"L={Escape(r.LeftText)}|R={Escape(r.RightText)}|" +
                $"Li={FormatSpans(r.LeftIntraLine)}|Ri={FormatSpans(r.RightIntraLine)}");
        }

        return string.Join('\n', lines);
    }

    private static string Escape(ReadOnlyMemory<char> text) =>
        text.ToString().Replace('\n', '⏎').Replace('\r', '␍');

    private static string FormatSpans(IReadOnlyList<CharSpan>? spans)
    {
        if (spans is null || spans.Count == 0) return "";
        return string.Join(',', spans.Select(s => $"{s.Start}+{s.Length}"));
    }
}

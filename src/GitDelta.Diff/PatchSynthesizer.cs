using System.Linq;
using System.Text;
using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.Diff;

/// <summary>Identifies one line within a hunk, by the same coordinates carried on <see cref="DiffRow"/>.</summary>
public readonly record struct LineSelection(int HunkIndex, int LineIndexInHunk);

/// <summary>
/// Produces valid patch text from a <see cref="FileDiff"/> for a selected subset of hunks or lines,
/// suitable for <c>git apply --cached</c> (stage) or <c>git apply --reverse</c> (unstage/discard).
///
/// Every emitted line's text comes verbatim from the corresponding <see cref="DiffLine.Text"/>
/// slice of the original <see cref="FileDiff.RawPatch"/>, so a synthesized patch matches Git's own
/// model exactly rather than a re-derived approximation.
///
/// Line selection follows the same convention as `git add -p`'s line-level staging: an unselected
/// added line is dropped entirely (as if it had never been added), and an unselected removed line is
/// converted to context (as if its removal had never been requested). Selected lines and all context
/// lines are kept as-is.
/// </summary>
public static class PatchSynthesizer
{
    /// <summary>Returns the diff's original, unmodified patch text — used for whole-file stage/discard.</summary>
    public static string SynthesizeWholeFile(FileDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return diff.RawPatch;
    }

    /// <summary>Synthesizes a patch containing exactly the given hunks, verbatim.</summary>
    public static string SynthesizeHunks(FileDiff diff, IReadOnlyCollection<int> hunkIndices)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(hunkIndices);
        if (diff.IsBinary)
            throw new InvalidOperationException("Cannot synthesize a partial patch for a binary file diff; stage or discard the whole file instead.");
        if (hunkIndices.Count == 0)
            throw new ArgumentException("At least one hunk must be selected.", nameof(hunkIndices));

        var sb = new StringBuilder();
        AppendFileHeader(sb, diff);

        var any = false;
        foreach (var index in hunkIndices.Distinct().OrderBy(i => i))
        {
            if (index < 0 || index >= diff.Hunks.Count)
                throw new ArgumentOutOfRangeException(nameof(hunkIndices), index, "Hunk index is out of range.");

            var hunkText = BuildHunkText(diff.Hunks[index], static _ => true, wholeHunk: true);
            if (hunkText.Length == 0)
                continue;

            sb.Append(hunkText);
            any = true;
        }

        if (!any)
            throw new ArgumentException("Selected hunks contain no changes.", nameof(hunkIndices));

        return sb.ToString();
    }

    /// <summary>Synthesizes a patch containing only the selected lines, converting unselected removals to context and dropping unselected additions.</summary>
    public static string SynthesizeLines(FileDiff diff, IReadOnlyCollection<LineSelection> selectedLines)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(selectedLines);
        if (diff.IsBinary)
            throw new InvalidOperationException("Cannot synthesize a partial patch for a binary file diff; stage or discard the whole file instead.");
        if (selectedLines.Count == 0)
            throw new ArgumentException("At least one line must be selected.", nameof(selectedLines));

        var byHunk = selectedLines
            .GroupBy(s => s.HunkIndex)
            .ToDictionary(g => g.Key, g => new HashSet<int>(g.Select(s => s.LineIndexInHunk)));

        var sb = new StringBuilder();
        AppendFileHeader(sb, diff);

        var any = false;
        foreach (var hunkIndex in byHunk.Keys.OrderBy(i => i))
        {
            if (hunkIndex < 0 || hunkIndex >= diff.Hunks.Count)
                throw new ArgumentOutOfRangeException(nameof(selectedLines), hunkIndex, "Hunk index is out of range.");

            var lineSet = byHunk[hunkIndex];
            var hunkText = BuildHunkText(diff.Hunks[hunkIndex], lineSet.Contains, wholeHunk: false);
            if (hunkText.Length == 0)
                continue;

            sb.Append(hunkText);
            any = true;
        }

        if (!any)
            throw new ArgumentException("Selected lines contain no changes to apply.", nameof(selectedLines));

        return sb.ToString();
    }

    private static void AppendFileHeader(StringBuilder sb, FileDiff diff)
    {
        var diffOldPath = diff.Change == ChangeKind.Added ? diff.NewPath.Value : diff.OldPath.Value;
        var diffNewPath = diff.Change == ChangeKind.Deleted ? diff.OldPath.Value : diff.NewPath.Value;

        sb.Append("diff --git a/").Append(diffOldPath).Append(" b/").Append(diffNewPath).Append('\n');

        switch (diff.Change)
        {
            case ChangeKind.Added:
                sb.Append("new file mode 100644\n");
                break;
            case ChangeKind.Deleted:
                sb.Append("deleted file mode 100644\n");
                break;
            case ChangeKind.Renamed:
                sb.Append("rename from ").Append(diff.OldPath.Value).Append('\n');
                sb.Append("rename to ").Append(diff.NewPath.Value).Append('\n');
                break;
            case ChangeKind.Copied:
                sb.Append("copy from ").Append(diff.OldPath.Value).Append('\n');
                sb.Append("copy to ").Append(diff.NewPath.Value).Append('\n');
                break;
        }

        var hasOld = !diff.OldContent.IsEmpty;
        var hasNew = !diff.NewContent.IsEmpty;
        if (hasOld || hasNew)
        {
            sb.Append("index ")
              .Append(hasOld ? diff.OldContent.Value : "0000000")
              .Append("..")
              .Append(hasNew ? diff.NewContent.Value : "0000000")
              .Append('\n');
        }

        sb.Append("--- ").Append(diff.Change == ChangeKind.Added ? "/dev/null" : "a/" + diff.OldPath.Value).Append('\n');
        sb.Append("+++ ").Append(diff.Change == ChangeKind.Deleted ? "/dev/null" : "b/" + diff.NewPath.Value).Append('\n');
    }

    /// <returns>The rendered hunk text, or an empty string if the selection leaves no actual change in the hunk.</returns>
    private static string BuildHunkText(DiffHunk hunk, Func<int, bool> isSelected, bool wholeHunk)
    {
        var body = new List<(char Marker, ReadOnlyMemory<char> Text)>(hunk.Lines.Count);
        var oldCount = 0;
        var newCount = 0;

        for (var i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];
            switch (line.Kind)
            {
                case DiffLineKind.Context:
                    body.Add((' ', line.Text));
                    oldCount++;
                    newCount++;
                    break;

                case DiffLineKind.Added:
                    if (wholeHunk || isSelected(i))
                    {
                        body.Add(('+', line.Text));
                        newCount++;
                    }
                    // Unselected addition: omitted entirely, as if it never happened.
                    break;

                case DiffLineKind.Removed:
                    if (wholeHunk || isSelected(i))
                    {
                        body.Add(('-', line.Text));
                        oldCount++;
                    }
                    else
                    {
                        // Unselected removal: kept, but as unchanged context.
                        body.Add((' ', line.Text));
                        oldCount++;
                        newCount++;
                    }
                    break;

                case DiffLineKind.NoNewlineAtEof:
                    // Only meaningful if the line it annotates is still represented as-is.
                    if (i > 0 && WasRepresented(hunk.Lines[i - 1], i - 1, isSelected, wholeHunk))
                        body.Add(('\\', line.Text));
                    break;
            }
        }

        if (!body.Any(l => l.Marker is '+' or '-'))
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("@@ -").Append(hunk.OldStart).Append(',').Append(oldCount)
          .Append(" +").Append(hunk.NewStart).Append(',').Append(newCount).Append(" @@\n");

        foreach (var (marker, text) in body)
        {
            sb.Append(marker);
            sb.Append(text.Span);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static bool WasRepresented(DiffLine line, int index, Func<int, bool> isSelected, bool wholeHunk) =>
        line.Kind switch
        {
            DiffLineKind.Context => true,
            DiffLineKind.Removed => true, // always represented: either kept as '-' or converted to context
            DiffLineKind.Added => wholeHunk || isSelected(index),
            _ => false,
        };
}

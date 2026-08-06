using System.Security.Cryptography;
using System.Text;
using GitDelta.Core;
using GitDelta.Core.AI;
using GitDelta.Core.Diff;

namespace GitDelta.Diff;

/// <summary>Builds Magic Commit inventories and fingerprints hunks for rematch after partial commits.</summary>
public static class MagicCommitInventory
{
    public static string FingerprintHunk(DiffHunk hunk)
    {
        ArgumentNullException.ThrowIfNull(hunk);
        var sb = new StringBuilder();
        foreach (var line in hunk.Lines)
        {
            sb.Append((char)LineKindChar(line.Kind));
            sb.Append(line.Text.Span);
            sb.Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16];
    }

    public static string FingerprintWholeFile(FileDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var payload = $"{diff.Change}|{diff.OldPath.Value}|{diff.NewPath.Value}|{diff.OldContent.Value}|{diff.NewContent.Value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// Builds inventory items from a set of file diffs. Binary / empty-hunk files become whole-file items.
    /// </summary>
    public static IReadOnlyList<MagicCommitHunkItem> Build(IReadOnlyList<FileDiff> diffs)
    {
        ArgumentNullException.ThrowIfNull(diffs);
        var items = new List<MagicCommitHunkItem>();
        var id = 1;

        foreach (var diff in diffs)
        {
            var path = diff.NewPath.Value.Length > 0 ? diff.NewPath.Value : diff.OldPath.Value;
            // Untracked must be whole-file: IndexToWorktree rematch is empty until staged.
            if (diff.IsBinary || diff.Hunks.Count == 0 || diff.Change == ChangeKind.Untracked)
            {
                var fp = FingerprintWholeFile(diff);
                items.Add(new MagicCommitHunkItem(
                    Id: $"h{id++}",
                    Path: path,
                    HunkIndex: -1,
                    Fingerprint: fp,
                    Header: $"{diff.Change} (whole file)",
                    Preview: Truncate(diff.RawPatch, 200),
                    WholeFile: true));
                continue;
            }

            for (var i = 0; i < diff.Hunks.Count; i++)
            {
                var hunk = diff.Hunks[i];
                items.Add(new MagicCommitHunkItem(
                    Id: $"h{id++}",
                    Path: path,
                    HunkIndex: i,
                    Fingerprint: FingerprintHunk(hunk),
                    Header: hunk.Header,
                    Preview: BuildPreview(hunk),
                    WholeFile: false));
            }
        }

        return items;
    }

    /// <summary>
    /// Finds the current hunk index in <paramref name="diff"/> whose fingerprint matches
    /// <paramref name="fingerprint"/>, or null when not found.
    /// </summary>
    public static int? FindHunkIndex(FileDiff diff, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(diff);
        for (var i = 0; i < diff.Hunks.Count; i++)
        {
            if (string.Equals(FingerprintHunk(diff.Hunks[i]), fingerprint, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return null;
    }

    public static string FormatInventoryForPrompt(IReadOnlyList<MagicCommitHunkItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.AppendLine($"ID={item.Id} PATH={item.Path} WHOLE={item.WholeFile} HEADER={item.Header}");
            sb.AppendLine(item.Preview);
            sb.AppendLine("---");
        }

        return sb.ToString();
    }

    private static string BuildPreview(DiffHunk hunk)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var line in hunk.Lines)
        {
            if (line.Kind is DiffLineKind.Context)
                continue;
            sb.Append((char)LineKindChar(line.Kind));
            sb.Append(line.Text.Span);
            sb.Append('\n');
            if (++count >= 12)
            {
                sb.AppendLine("…");
                break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;
        return text[..max] + "…";
    }

    private static byte LineKindChar(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => (byte)'+',
        DiffLineKind.Removed => (byte)'-',
        DiffLineKind.Context => (byte)' ',
        _ => (byte)'?',
    };
}

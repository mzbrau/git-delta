using GitDelta.Core;

namespace GitDelta.Git;

/// <summary>Result of parsing `git status --porcelain=v2 -z` output.</summary>
public sealed record PorcelainStatusResult(
    IReadOnlyList<StatusEntry> Staged,
    IReadOnlyList<StatusEntry> Unstaged,
    IReadOnlyList<StatusEntry> Conflicted,
    string? CurrentBranch);

/// <summary>
/// Parses `git status --porcelain=v2 -z` output.
///
/// Record shapes (see git-status(1), "Porcelain Format Version 2"), NUL-delimited fields:
/// <list type="bullet">
/// <item><c>1 XY sub mH mI mW hH hI path</c> — ordinary changed entry.</item>
/// <item><c>2 XY sub mH mI mW hH hI Xscore path\0origPath</c> — renamed or copied entry; the
/// rename source path is a second NUL-delimited token belonging to the same logical record.</item>
/// <item><c>u XY sub m1 m2 m3 mW h1 h2 h3 path</c> — unmerged entry, carrying all three stages.</item>
/// <item><c>? path</c> — untracked.</item>
/// <item><c>! path</c> — ignored (only present with <c>--ignored</c>).</item>
/// </list>
/// A single ordinary/rename record describes both the index (X) and worktree (Y) state of one
/// path. A partially staged file therefore yields one <see cref="StatusEntry"/> in
/// <see cref="PorcelainStatusResult.Staged"/> (from X) and a separate one in
/// <see cref="PorcelainStatusResult.Unstaged"/> (from Y), matching the two-list working copy model.
/// </summary>
public static class PorcelainStatusParser
{
    public static PorcelainStatusResult Parse(string rawOutput)
    {
        var staged = new List<StatusEntry>();
        var unstaged = new List<StatusEntry>();
        var conflicted = new List<StatusEntry>();
        string? currentBranch = null;

        var tokens = rawOutput.Split('\0');
        var i = 0;
        while (i < tokens.Length)
        {
            var token = tokens[i];
            if (token.Length == 0)
            {
                i++;
                continue;
            }

            switch (token[0])
            {
                case '#':
                    var branch = TryParseBranchHeader(token);
                    if (branch is not null)
                        currentBranch = branch;
                    i++;
                    break;

                case '1':
                    ParseOrdinary(token, staged, unstaged);
                    i++;
                    break;

                case '2':
                    var origPath = i + 1 < tokens.Length ? tokens[i + 1] : string.Empty;
                    ParseRenameOrCopy(token, origPath, staged, unstaged);
                    i += 2;
                    break;

                case 'u':
                    ParseUnmerged(token, conflicted);
                    i++;
                    break;

                case '?':
                    ParseUntracked(token, unstaged);
                    i++;
                    break;

                case '!':
                    // Ignored entries are not surfaced in Phase 1's file lists.
                    i++;
                    break;

                default:
                    i++;
                    break;
            }
        }

        return new PorcelainStatusResult(staged, unstaged, conflicted, currentBranch);
    }

    private static string? TryParseBranchHeader(string token)
    {
        const string headPrefix = "# branch.head ";
        if (!token.StartsWith(headPrefix, StringComparison.Ordinal))
            return null;

        var name = token[headPrefix.Length..].Trim();
        return name is "(detached)" or "" ? null : name;
    }

    private static void ParseOrdinary(string token, List<StatusEntry> staged, List<StatusEntry> unstaged)
    {
        // "1 XY sub mH mI mW hH hI path"
        var fields = token.Split(' ', 9);
        if (fields.Length < 9)
            return;

        var xy = fields[1];
        var headOid = ParseOid(fields[6]);
        var indexOid = ParseOid(fields[7]);
        var path = FilePath.From(fields[8]);

        AddIndexAndWorktreeEntries(xy, path, originalPath: null, headOid, indexOid, staged, unstaged);
    }

    private static void ParseRenameOrCopy(string token, string origPath, List<StatusEntry> staged, List<StatusEntry> unstaged)
    {
        // "2 XY sub mH mI mW hH hI Xscore path" (origPath is the following NUL-delimited token)
        var fields = token.Split(' ', 10);
        if (fields.Length < 10)
            return;

        var xy = fields[1];
        var headOid = ParseOid(fields[6]);
        var indexOid = ParseOid(fields[7]);
        var path = FilePath.From(fields[9]);
        var original = FilePath.From(origPath);

        AddIndexAndWorktreeEntries(xy, path, original, headOid, indexOid, staged, unstaged);
    }

    private static void AddIndexAndWorktreeEntries(
        string xy,
        FilePath path,
        FilePath? originalPath,
        ContentId? headOid,
        ContentId? indexOid,
        List<StatusEntry> staged,
        List<StatusEntry> unstaged)
    {
        var x = xy[0];
        var y = xy.Length > 1 ? xy[1] : '.';

        if (x != '.')
        {
            staged.Add(new StatusEntry(
                path,
                originalPath,
                ToChangeKind(x, originalPath is not null),
                IsStaged: true,
                IsUnstaged: false,
                IsConflicted: false,
                indexOid,
                WorktreeOid: null,
                headOid));
        }

        if (y != '.')
        {
            unstaged.Add(new StatusEntry(
                path,
                originalPath,
                ToChangeKind(y, originalPath is not null),
                IsStaged: false,
                IsUnstaged: true,
                IsConflicted: false,
                indexOid,
                WorktreeOid: null,
                headOid));
        }
    }

    private static void ParseUnmerged(string token, List<StatusEntry> conflicted)
    {
        // "u XY sub m1 m2 m3 mW h1 h2 h3 path"
        var fields = token.Split(' ', 11);
        if (fields.Length < 11)
            return;

        var baseOid = ParseOid(fields[7]);
        var oursOid = ParseOid(fields[8]);
        var theirsOid = ParseOid(fields[9]);
        var path = FilePath.From(fields[10]);

        conflicted.Add(new StatusEntry(
            path,
            OriginalPath: null,
            ChangeKind.Conflicted,
            IsStaged: false,
            IsUnstaged: false,
            IsConflicted: true,
            IndexOid: oursOid,
            WorktreeOid: theirsOid,
            HeadOid: baseOid));
    }

    private static void ParseUntracked(string token, List<StatusEntry> unstaged)
    {
        // "? path"
        var path = token.Length > 2 ? token[2..] : string.Empty;
        if (path.Length == 0)
            return;

        unstaged.Add(new StatusEntry(
            FilePath.From(path),
            OriginalPath: null,
            ChangeKind.Untracked,
            IsStaged: false,
            IsUnstaged: true,
            IsConflicted: false));
    }

    private static ContentId? ParseOid(string field) =>
        string.IsNullOrEmpty(field) || field == "0000000000000000000000000000000000000000"
            ? null
            : ContentId.FromSha(field);

    private static ChangeKind ToChangeKind(char statusChar, bool isRenameRecord)
    {
        if (isRenameRecord && (statusChar is 'R' or 'C'))
            return statusChar == 'R' ? ChangeKind.Renamed : ChangeKind.Copied;

        return statusChar switch
        {
            'M' => ChangeKind.Modified,
            'T' => ChangeKind.TypeChanged,
            'A' => ChangeKind.Added,
            'D' => ChangeKind.Deleted,
            'R' => ChangeKind.Renamed,
            'C' => ChangeKind.Copied,
            'U' => ChangeKind.Conflicted,
            _ => ChangeKind.Modified,
        };
    }
}

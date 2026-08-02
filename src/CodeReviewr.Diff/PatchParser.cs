using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.Diff;

/// <summary>
/// The header portion of a parsed patch: everything Git prints before the first hunk.
/// Exposed independently of <see cref="FileDiff.Hunks"/> so callers can display file metadata
/// (path, rename, binary) before hunk parsing completes.
/// </summary>
public sealed record PatchHeader(
    DiffScope Scope,
    FilePath OldPath,
    FilePath NewPath,
    ChangeKind Change,
    ContentId OldContent,
    ContentId NewContent,
    bool IsBinary,
    string RawPatch)
{
    public FileDiff ToFileDiff(IReadOnlyList<DiffHunk> hunks) =>
        new(Scope, OldPath, NewPath, Change, OldContent, NewContent, IsBinary, hunks, RawPatch);
}

/// <summary>
/// Parses unified diff patch text (as produced by `git diff`) into the canonical <see cref="FileDiff"/>
/// model. <see cref="DiffLine.Text"/> is always a <see cref="ReadOnlyMemory{T}"/> slice into the
/// original <paramref name="RawPatch"/> string passed in, so a large diff costs one allocation for
/// the raw text rather than one per line.
///
/// Assumes <c>rawPatch</c> describes a single file, which is what `git diff -- &lt;path&gt;` produces.
/// </summary>
public static class PatchParser
{
    // Deliberately unanchored: callers verify `match.Index == pos` themselves, because
    // Regex.Match(input, beginning, length) does not treat `beginning` as a fresh "^" origin.
    private static readonly Regex HunkHeaderRegex = new(
        @"@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses the full patch, including all hunks, synchronously.</summary>
    public static FileDiff Parse(string rawPatch, DiffTarget target) =>
        Parse(rawPatch, target.AsWorkingCopy());

    public static FileDiff Parse(string rawPatch, DiffScope scope)
    {
        ArgumentNullException.ThrowIfNull(rawPatch);

        var scan = ScanHeader(rawPatch);
        var header = BuildHeader(rawPatch, scope, scan);
        var hunks = scan.IsBinary
            ? (IReadOnlyList<DiffHunk>)Array.Empty<DiffHunk>()
            : ParseHunksCore(rawPatch, scan.BodyStart).ToList();
        return header.ToFileDiff(hunks);
    }

    /// <summary>Parses only the header, without walking hunk bodies. Cheap: one pass over the header lines.</summary>
    public static PatchHeader ParseHeader(string rawPatch, DiffTarget target) =>
        ParseHeader(rawPatch, target.AsWorkingCopy());

    public static PatchHeader ParseHeader(string rawPatch, DiffScope scope)
    {
        ArgumentNullException.ThrowIfNull(rawPatch);

        var scan = ScanHeader(rawPatch);
        return BuildHeader(rawPatch, scope, scan);
    }

    /// <summary>
    /// Streams hunks as they are parsed, yielding control back to the caller periodically so a very
    /// large single-file diff does not monopolise a thread. Supports the "publish hunks as parsed"
    /// progressive-loading requirement directly via <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    public static async IAsyncEnumerable<DiffHunk> ParseHunksAsync(
        string rawPatch,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawPatch);

        var scan = ScanHeader(rawPatch);
        if (scan.IsBinary)
            yield break;

        var count = 0;
        foreach (var hunk in ParseHunksCore(rawPatch, scan.BodyStart))
        {
            ct.ThrowIfCancellationRequested();
            yield return hunk;
            if (++count % 8 == 0)
                await Task.Yield();
        }
    }

    /// <summary>
    /// Callback-based progressive parse: invokes <paramref name="onHunkParsed"/> as each hunk is
    /// parsed (e.g. to publish it to a UI immediately) and returns the fully assembled
    /// <see cref="FileDiff"/> once every hunk has been processed.
    /// </summary>
    public static async Task<FileDiff> ParseProgressiveAsync(
        string rawPatch,
        DiffTarget target,
        Action<DiffHunk>? onHunkParsed = null,
        CancellationToken ct = default) =>
        await ParseProgressiveAsync(rawPatch, target.AsWorkingCopy(), onHunkParsed, ct).ConfigureAwait(false);

    public static async Task<FileDiff> ParseProgressiveAsync(
        string rawPatch,
        DiffScope scope,
        Action<DiffHunk>? onHunkParsed = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawPatch);

        var scan = ScanHeader(rawPatch);
        var header = BuildHeader(rawPatch, scope, scan);

        var hunks = new List<DiffHunk>();
        if (!scan.IsBinary)
        {
            var count = 0;
            foreach (var hunk in ParseHunksCore(rawPatch, scan.BodyStart))
            {
                ct.ThrowIfCancellationRequested();
                hunks.Add(hunk);
                onHunkParsed?.Invoke(hunk);
                if (++count % 8 == 0)
                    await Task.Yield();
            }
        }

        return header.ToFileDiff(hunks);
    }

    private static PatchHeader BuildHeader(string rawPatch, DiffScope scope, HeaderScan scan) =>
        new(scope, scan.OldPath, scan.NewPath, scan.Change, scan.OldContent, scan.NewContent, scan.IsBinary, rawPatch);

    private readonly record struct HeaderScan(
        FilePath OldPath,
        FilePath NewPath,
        ChangeKind Change,
        ContentId OldContent,
        ContentId NewContent,
        bool IsBinary,
        int BodyStart);

    private static HeaderScan ScanHeader(string rawPatch)
    {
        string? diffGitOld = null, diffGitNew = null;
        string? renameFrom = null, renameTo = null;
        string? copyFrom = null, copyTo = null;
        var isNewFile = false;
        var isDeletedFile = false;
        string? minusPath = null, plusPath = null;
        var minusIsDevNull = false;
        var plusIsDevNull = false;
        string? oldSha = null, newSha = null;
        var isBinary = false;

        var pos = 0;
        var bodyStart = rawPatch.Length;

        while (pos < rawPatch.Length)
        {
            var newlineIdx = rawPatch.IndexOf('\n', pos);
            var lineEnd = newlineIdx == -1 ? rawPatch.Length : newlineIdx;
            var rawLine = rawPatch.AsSpan(pos, lineEnd - pos);
            var line = rawLine.Length > 0 && rawLine[^1] == '\r' ? rawLine[..^1] : rawLine;

            if (line.StartsWith("@@ "))
            {
                bodyStart = pos;
                break;
            }

            if (line.StartsWith("diff --git "))
            {
                ParseDiffGitLine(line, out diffGitOld, out diffGitNew);
            }
            else if (line.StartsWith("rename from "))
            {
                renameFrom = Unquote(line["rename from ".Length..].ToString());
            }
            else if (line.StartsWith("rename to "))
            {
                renameTo = Unquote(line["rename to ".Length..].ToString());
            }
            else if (line.StartsWith("copy from "))
            {
                copyFrom = Unquote(line["copy from ".Length..].ToString());
            }
            else if (line.StartsWith("copy to "))
            {
                copyTo = Unquote(line["copy to ".Length..].ToString());
            }
            else if (line.StartsWith("new file mode"))
            {
                isNewFile = true;
            }
            else if (line.StartsWith("deleted file mode"))
            {
                isDeletedFile = true;
            }
            else if (line.StartsWith("index "))
            {
                ParseIndexLine(line, out oldSha, out newSha);
            }
            else if (line.StartsWith("--- "))
            {
                var (path, isDevNull) = ParsePathAfterMarker(line, "--- ".Length);
                minusPath = path;
                minusIsDevNull = isDevNull;
            }
            else if (line.StartsWith("+++ "))
            {
                var (path, isDevNull) = ParsePathAfterMarker(line, "+++ ".Length);
                plusPath = path;
                plusIsDevNull = isDevNull;
            }
            else if (line.StartsWith("Binary files ") || line.StartsWith("GIT binary patch"))
            {
                isBinary = true;
            }
            // "old mode"/"new mode"/"similarity index"/"dissimilarity index" carry no information
            // our model represents; skip them.

            if (newlineIdx == -1)
            {
                pos = rawPatch.Length;
                break;
            }

            pos = newlineIdx + 1;
        }

        var isRename = renameFrom is not null || renameTo is not null;
        var isCopy = !isRename && (copyFrom is not null || copyTo is not null);

        ChangeKind change;
        if (isRename) change = ChangeKind.Renamed;
        else if (isCopy) change = ChangeKind.Copied;
        else if (isNewFile || minusIsDevNull) change = ChangeKind.Added;
        else if (isDeletedFile || plusIsDevNull) change = ChangeKind.Deleted;
        else change = ChangeKind.Modified;

        var resolvedOld = renameFrom ?? copyFrom ?? (minusIsDevNull ? null : minusPath) ?? diffGitOld;
        var resolvedNew = renameTo ?? copyTo ?? (plusIsDevNull ? null : plusPath) ?? diffGitNew;
        resolvedOld ??= resolvedNew;
        resolvedNew ??= resolvedOld;

        var oldPath = FilePath.From(resolvedOld ?? string.Empty);
        var newPath = FilePath.From(resolvedNew ?? string.Empty);
        var oldContent = ShaToContentId(oldSha);
        var newContent = ShaToContentId(newSha);

        return new HeaderScan(oldPath, newPath, change, oldContent, newContent, isBinary, bodyStart);
    }

    private static IEnumerable<DiffHunk> ParseHunksCore(string rawPatch, int bodyStart)
    {
        var memory = rawPatch.AsMemory();
        var pos = bodyStart;

        while (pos < rawPatch.Length)
        {
            var newlineIdx = rawPatch.IndexOf('\n', pos);
            var lineEnd = newlineIdx == -1 ? rawPatch.Length : newlineIdx;

            var match = HunkHeaderRegex.Match(rawPatch, pos, lineEnd - pos);
            if (!match.Success || match.Index != pos)
            {
                // Defensive skip of unrecognised content between hunks (should not occur for
                // well-formed `git diff` output for a single file).
                pos = newlineIdx == -1 ? rawPatch.Length : newlineIdx + 1;
                continue;
            }

            var oldStart = int.Parse(match.Groups["oldStart"].ValueSpan);
            var oldCount = match.Groups["oldCount"].Success ? int.Parse(match.Groups["oldCount"].ValueSpan) : 1;
            var newStart = int.Parse(match.Groups["newStart"].ValueSpan);
            var newCount = match.Groups["newCount"].Success ? int.Parse(match.Groups["newCount"].ValueSpan) : 1;

            var headerSpan = rawPatch.AsSpan(pos, lineEnd - pos);
            if (headerSpan.Length > 0 && headerSpan[^1] == '\r')
                headerSpan = headerSpan[..^1];
            var headerText = headerSpan.ToString();

            pos = newlineIdx == -1 ? rawPatch.Length : newlineIdx + 1;

            var lines = new List<DiffLine>();
            var oldLine = oldStart;
            var newLine = newStart;

            while (pos < rawPatch.Length)
            {
                var marker = rawPatch[pos];
                if (marker != ' ' && marker != '+' && marker != '-' && marker != '\\')
                    break;

                var nlIdx = rawPatch.IndexOf('\n', pos);
                var lEnd = nlIdx == -1 ? rawPatch.Length : nlIdx;
                var contentStart = pos + 1;
                var contentLen = Math.Max(0, lEnd - contentStart);
                // CRLF patches (and Windows-checked-out test fixtures) leave a CR before \n.
                if (contentLen > 0 && rawPatch[contentStart + contentLen - 1] == '\r')
                    contentLen--;
                var text = contentLen > 0 ? memory.Slice(contentStart, contentLen) : ReadOnlyMemory<char>.Empty;

                switch (marker)
                {
                    case ' ':
                        lines.Add(new DiffLine(DiffLineKind.Context, oldLine, newLine, text));
                        oldLine++;
                        newLine++;
                        break;
                    case '-':
                        lines.Add(new DiffLine(DiffLineKind.Removed, oldLine, null, text));
                        oldLine++;
                        break;
                    case '+':
                        lines.Add(new DiffLine(DiffLineKind.Added, null, newLine, text));
                        newLine++;
                        break;
                    case '\\':
                        lines.Add(new DiffLine(DiffLineKind.NoNewlineAtEof, null, null, text));
                        break;
                }

                pos = nlIdx == -1 ? rawPatch.Length : nlIdx + 1;
            }

            yield return new DiffHunk(oldStart, oldCount, newStart, newCount, headerText, lines);
        }
    }

    private static void ParseDiffGitLine(ReadOnlySpan<char> line, out string? oldPath, out string? newPath)
    {
        oldPath = null;
        newPath = null;

        const string prefix = "diff --git ";
        if (!line.StartsWith(prefix))
            return;

        var rest = line[prefix.Length..].ToString();

        // Paths may contain spaces, so this is a heuristic: split on the last " b/" occurrence.
        // Precise disambiguation for such paths comes from "index"/"rename"/"---"/"+++" lines,
        // which are preferred over this fallback wherever present.
        var sep = rest.LastIndexOf(" b/", StringComparison.Ordinal);
        if (sep < 0)
            return;

        var a = rest[..sep];
        var b = rest[(sep + 3)..];
        if (a.StartsWith("a/", StringComparison.Ordinal))
            a = a[2..];

        oldPath = Unquote(a);
        newPath = Unquote(b);
    }

    private static void ParseIndexLine(ReadOnlySpan<char> line, out string? oldSha, out string? newSha)
    {
        oldSha = null;
        newSha = null;

        const string prefix = "index ";
        if (!line.StartsWith(prefix))
            return;

        var rest = line[prefix.Length..];
        var spaceIdx = rest.IndexOf(' ');
        var shaPart = (spaceIdx < 0 ? rest : rest[..spaceIdx]).ToString();

        var dotDot = shaPart.IndexOf("..", StringComparison.Ordinal);
        if (dotDot < 0)
        {
            oldSha = shaPart;
            return;
        }

        oldSha = shaPart[..dotDot];
        newSha = shaPart[(dotDot + 2)..];
    }

    private static (string? Path, bool IsDevNull) ParsePathAfterMarker(ReadOnlySpan<char> line, int markerLength)
    {
        var rest = line[markerLength..];
        if (rest.SequenceEqual("/dev/null"))
            return (null, true);

        var restStr = rest.ToString();
        if (restStr.StartsWith("a/", StringComparison.Ordinal) || restStr.StartsWith("b/", StringComparison.Ordinal))
            restStr = restStr[2..];

        return (Unquote(restStr), false);
    }

    private static ContentId ShaToContentId(string? sha)
    {
        if (string.IsNullOrEmpty(sha))
            return ContentId.Empty;
        if (sha.All(c => c == '0'))
            return ContentId.Empty;
        return ContentId.FromSha(sha);
    }

    /// <summary>
    /// Undoes Git's C-style quoting of paths (used for non-ASCII or otherwise "unusual" paths when
    /// <c>core.quotePath</c> is not disabled). Handles octal byte escapes and common C escapes.
    /// </summary>
    private static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"')
            return s;

        var inner = s.AsSpan(1, s.Length - 2);
        var bytes = new List<byte>(inner.Length);
        var i = 0;
        Span<char> ch = stackalloc char[1];
        Span<byte> buf = stackalloc byte[4];

        while (i < inner.Length)
        {
            var c = inner[i];
            if (c == '\\' && i + 1 < inner.Length)
            {
                var next = inner[i + 1];
                if (next is >= '0' and <= '7')
                {
                    var val = 0;
                    var digits = 0;
                    var j = i + 1;
                    while (digits < 3 && j < inner.Length && inner[j] is >= '0' and <= '7')
                    {
                        val = val * 8 + (inner[j] - '0');
                        j++;
                        digits++;
                    }

                    bytes.Add((byte)val);
                    i = j;
                    continue;
                }

                switch (next)
                {
                    case '"': bytes.Add((byte)'"'); break;
                    case '\\': bytes.Add((byte)'\\'); break;
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    default: bytes.Add((byte)next); break;
                }

                i += 2;
                continue;
            }

            ch[0] = c;
            var n = System.Text.Encoding.UTF8.GetBytes(ch, buf);
            for (var k = 0; k < n; k++)
                bytes.Add(buf[k]);
            i++;
        }

        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}

using System.Globalization;
using GitDelta.Core;
using GitDelta.Core.Abstractions;

namespace GitDelta.Git;

/// <summary>Commit history listing and per-file commit diffs.</summary>
public sealed class GitHistoryService(
    IGitProcessRunner runner,
    IRepositoryGateProvider gates,
    ISettingsStore? settings = null) : IGitHistoryService
{
    // Record = RS (\x1e), field = US (\x1f). Body may contain newlines; it must not contain RS/US.
    private const string LogFormat =
        "%x1e%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%b%x1f%D";

    private const char RecordSep = '\x1e';
    private const char FieldSep = '\x1f';

    public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(
        string repositoryPath,
        int skip,
        int take,
        string revision = "HEAD",
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            if (take <= 0)
                return (IReadOnlyList<CommitInfo>)Array.Empty<CommitInfo>();

            var rev = string.IsNullOrWhiteSpace(revision) ? "HEAD" : revision;
            var args = new List<string>
            {
                "log",
                rev,
                $"--skip={Math.Max(0, skip)}",
                $"--max-count={take}",
                $"--format={LogFormat}",
            };

            var result = await runner.RunAsync(repositoryPath, args, options: null, token)
                .ConfigureAwait(false);
            return (IReadOnlyList<CommitInfo>)ParseCommitLog(result.Stdout);
        }, ct), ct);

    public Task<IReadOnlyList<CommitInfo>> ListCommitsRangeAsync(
        string repositoryPath,
        string baseRef,
        string headRef = "HEAD",
        bool oldestFirst = false,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(baseRef) || string.IsNullOrWhiteSpace(headRef))
                return (IReadOnlyList<CommitInfo>)Array.Empty<CommitInfo>();

            var args = new List<string>
            {
                "log",
                $"{baseRef}..{headRef}",
                $"--format={LogFormat}",
            };
            if (oldestFirst)
                args.Add("--reverse");

            var result = await runner.RunAsync(repositoryPath, args, options: null, token)
                .ConfigureAwait(false);
            return (IReadOnlyList<CommitInfo>)ParseCommitLog(result.Stdout);
        }, ct), ct);

    public Task<IReadOnlyList<CommitInfo>> ListFileHistoryAsync(
        string repositoryPath,
        string path,
        int take,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            if (take <= 0 || string.IsNullOrWhiteSpace(path))
                return (IReadOnlyList<CommitInfo>)Array.Empty<CommitInfo>();

            var args = new List<string>
            {
                "log",
                "HEAD",
                "--follow",
                $"--max-count={take}",
                $"--format={LogFormat}",
                "--",
                path,
            };

            var result = await runner.RunAsync(repositoryPath, args, options: null, token)
                .ConfigureAwait(false);
            return (IReadOnlyList<CommitInfo>)ParseCommitLog(result.Stdout);
        }, ct), ct);

    public Task<CommitInfo?> GetFileCreatedCommitAsync(
        string repositoryPath,
        string path,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return (CommitInfo?)null;

            var args = new List<string>
            {
                "log",
                "HEAD",
                "--follow",
                "--diff-filter=A",
                "--max-count=1",
                $"--format={LogFormat}",
                "--",
                path,
            };

            var result = await runner.RunAsync(repositoryPath, args, options: null, token)
                .ConfigureAwait(false);
            var commits = ParseCommitLog(result.Stdout);
            return commits.Count > 0 ? commits[0] : null;
        }, ct), ct);

    public Task<IReadOnlyList<FilePath>> ListTrackedFilesAsync(
        string repositoryPath,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                repositoryPath,
                ["ls-files", "-z"],
                options: null,
                token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(result.Stdout))
                return (IReadOnlyList<FilePath>)Array.Empty<FilePath>();

            var list = new List<FilePath>();
            var span = result.Stdout.AsSpan();
            while (!span.IsEmpty)
            {
                var idx = span.IndexOf('\0');
                ReadOnlySpan<char> part;
                if (idx < 0)
                {
                    part = span;
                    span = default;
                }
                else
                {
                    part = span[..idx];
                    span = span[(idx + 1)..];
                }

                if (part.IsEmpty)
                    continue;

                list.Add(FilePath.From(part.ToString()));
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
            return (IReadOnlyList<FilePath>)list;
        }, ct), ct);

    public Task<IReadOnlyList<(FilePath Path, ChangeKind Kind)>> GetCommitFilesAsync(
        string repositoryPath,
        string oid,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                repositoryPath,
                ["show", "--name-status", "--format=", oid],
                options: null,
                token).ConfigureAwait(false);

            return (IReadOnlyList<(FilePath Path, ChangeKind Kind)>)
                GitStashService.ParseNameStatus(result.Stdout);
        }, ct), ct);

    public Task<CommitStat> GetCommitStatAsync(
        string repositoryPath,
        string oid,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                repositoryPath,
                ["show", "--numstat", "--format=", oid],
                options: null,
                token).ConfigureAwait(false);

            return ParseNumstat(oid, result.Stdout);
        }, ct), ct);

    public Task<string> GetCommitPatchAsync(
        string repositoryPath,
        string oid,
        FilePath path,
        DiffOptions options,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            // Resolve the path as it existed in oid (follows renames from the current path).
            // Using the current path alone after --follow listing yields empty create patches
            // and full-file adds for rename commits.
            var atCommit = await ResolvePathAtCommitAsync(repositoryPath, oid, path.Value, token)
                .ConfigureAwait(false);

            // `git show` handles root commits; `--format=` suppresses the commit header.
            var args = new List<string>
            {
                "show",
                "--format=",
                $"--diff-algorithm={options.Algorithm}",
                $"-U{options.ContextLines}",
                "--no-color",
                "--no-ext-diff",
            };

            if (options.DetectRenames)
                args.Add("-M");
            if (options.DetectCopies)
                args.Add("-C");
            if (options.IgnoreAllSpace)
                args.Add("-w");
            if (options.IgnoreSpaceChange)
                args.Add("--ignore-space-change");
            if (options.IgnoreBlankLines)
                args.Add("--ignore-blank-lines");

            args.Add(oid);
            args.Add("--");
            if (atCommit.OldPath is { Length: > 0 } oldPath)
                args.Add(oldPath);
            args.Add(atCommit.Path);

            var maxBytes = settings?.Current.MaxDiffPatchBytes ?? 32 * 1024 * 1024;
            var processOptions = new GitProcessOptions { MaxStdoutBytes = maxBytes };
            var result = await runner.RunAsync(repositoryPath, args, processOptions, token)
                .ConfigureAwait(false);
            return result.Stdout;
        }, ct), ct);

    private async Task<PathAtCommit> ResolvePathAtCommitAsync(
        string repositoryPath,
        string oid,
        string currentPath,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(oid) || string.IsNullOrWhiteSpace(currentPath))
            return new PathAtCommit(currentPath, null);

        var args = new List<string>
        {
            "log",
            "HEAD",
            "-M",
            "--follow",
            "--name-status",
            "--format=%H",
            "--",
            currentPath,
        };

        var result = await runner.RunAsync(repositoryPath, args, options: null, token)
            .ConfigureAwait(false);
        return ParsePathAtCommit(result.Stdout, oid) ?? new PathAtCommit(currentPath, null);
    }

    /// <summary>
    /// Parses <c>git log --follow --name-status --format=%H</c> output for the name-status
    /// entry belonging to <paramref name="oid"/> (full or unique prefix).
    /// </summary>
    public static PathAtCommit? ParsePathAtCommit(string stdout, string oid)
    {
        if (string.IsNullOrEmpty(stdout) || string.IsNullOrWhiteSpace(oid))
            return null;

        string? currentOid = null;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            if (LooksLikeCommitOid(line))
            {
                currentOid = line;
                continue;
            }

            if (currentOid is null || !OidEqualsOrPrefix(currentOid, oid))
                continue;

            var tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;

            var status = line[..tab].Trim();
            var pathPart = line[(tab + 1)..];
            if (status.Length > 0 && (status[0] == 'R' || status[0] == 'C'))
            {
                var mid = pathPart.IndexOf('\t');
                if (mid > 0)
                {
                    var oldPath = pathPart[..mid];
                    var newPath = pathPart[(mid + 1)..];
                    if (!string.IsNullOrWhiteSpace(newPath))
                        return new PathAtCommit(newPath, string.IsNullOrWhiteSpace(oldPath) ? null : oldPath);
                }
            }

            // Non-rename: path is the sole field (or last field if unexpected tabs).
            var lastTab = pathPart.LastIndexOf('\t');
            if (lastTab >= 0)
                pathPart = pathPart[(lastTab + 1)..];
            if (!string.IsNullOrWhiteSpace(pathPart))
                return new PathAtCommit(pathPart, null);
        }

        return null;
    }

    private static bool LooksLikeCommitOid(string line)
    {
        if (line.Length < 7 || line.Length > 40)
            return false;
        foreach (var c in line)
        {
            var isHex = (c >= '0' && c <= '9')
                        || (c >= 'a' && c <= 'f')
                        || (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }

        return true;
    }

    private static bool OidEqualsOrPrefix(string fullOid, string candidate)
    {
        if (fullOid.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            return true;
        // Allow unique-prefix matching (UI may pass short OIDs).
        if (candidate.Length >= 7
            && fullOid.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            return true;
        if (fullOid.Length >= 7
            && candidate.StartsWith(fullOid, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>Path of a followed file as it existed in a commit, plus rename/copy source when present.</summary>
    public readonly record struct PathAtCommit(string Path, string? OldPath);

    public static List<CommitInfo> ParseCommitLog(string stdout)
    {
        var list = new List<CommitInfo>();
        if (string.IsNullOrEmpty(stdout))
            return list;

        var records = stdout.Split(RecordSep, StringSplitOptions.RemoveEmptyEntries);
        foreach (var record in records)
        {
            var fields = record.Split(FieldSep);
            // Expected: H, h, P, an, ae, aI, s, b, D  (9 fields; body may be empty)
            if (fields.Length < 7)
                continue;

            var oid = fields[0].Trim();
            if (string.IsNullOrEmpty(oid))
                continue;

            var shortOid = fields.Length > 1 ? fields[1].Trim() : oid.Length >= 7 ? oid[..7] : oid;
            var parentsRaw = fields.Length > 2 ? fields[2].Trim() : "";
            var authorName = fields.Length > 3 ? fields[3] : "";
            var authorEmail = fields.Length > 4 ? fields[4] : "";
            var dateRaw = fields.Length > 5 ? fields[5].Trim() : "";
            var subject = fields.Length > 6 ? fields[6] : "";
            var body = fields.Length > 7 ? fields[7].TrimEnd('\n', '\r') : "";
            var decorationsRaw = fields.Length > 8 ? fields[8].Trim() : "";

            var parents = string.IsNullOrWhiteSpace(parentsRaw)
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : parentsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (!DateTimeOffset.TryParse(
                    dateRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var authorDate))
            {
                authorDate = DateTimeOffset.UnixEpoch;
            }

            var decorations = ParseDecorations(decorationsRaw);

            list.Add(new CommitInfo(
                oid,
                shortOid,
                subject,
                body,
                authorName,
                authorEmail,
                authorDate,
                parents,
                decorations));
        }

        return list;
    }

    internal static CommitStat ParseNumstat(string oid, string stdout)
    {
        var fileCount = 0;
        var insertions = 0;
        var deletions = 0;

        if (!string.IsNullOrEmpty(stdout))
        {
            using var reader = new StringReader(stdout);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split('\t');
                if (parts.Length < 3)
                    continue;

                fileCount++;
                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var added))
                    insertions += added;
                if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var removed))
                    deletions += removed;
            }
        }

        return new CommitStat(oid, fileCount, insertions, deletions);
    }

    private static IReadOnlyList<string> ParseDecorations(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        // e.g. "HEAD -> main, origin/main, tag: v1.0"
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var s = part.Trim();
            if (s.StartsWith("HEAD -> ", StringComparison.Ordinal))
                s = s["HEAD -> ".Length..].Trim();
            else if (s.StartsWith("tag: ", StringComparison.Ordinal))
                s = s["tag: ".Length..].Trim();

            if (!string.IsNullOrEmpty(s))
                list.Add(s);
        }

        return list;
    }
}

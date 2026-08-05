using System.Globalization;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.Git;

/// <summary>Commit history listing and per-file commit diffs.</summary>
public sealed class GitHistoryService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitHistoryService
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
        CancellationToken ct = default) =>
        gates.For(repositoryPath).RunReadAsync(async token =>
        {
            if (take <= 0)
                return (IReadOnlyList<CommitInfo>)Array.Empty<CommitInfo>();

            var args = new List<string>
            {
                "log",
                "HEAD",
                $"--skip={Math.Max(0, skip)}",
                $"--max-count={take}",
                $"--format={LogFormat}",
            };

            var result = await runner.RunAsync(repositoryPath, args, options: null, token)
                .ConfigureAwait(false);
            return (IReadOnlyList<CommitInfo>)ParseCommitLog(result.Stdout);
        }, ct);

    public Task<IReadOnlyList<CommitInfo>> ListCommitsRangeAsync(
        string repositoryPath,
        string baseRef,
        string headRef = "HEAD",
        bool oldestFirst = false,
        CancellationToken ct = default) =>
        gates.For(repositoryPath).RunReadAsync(async token =>
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
        }, ct);

    public Task<IReadOnlyList<CommitInfo>> ListFileHistoryAsync(
        string repositoryPath,
        string path,
        int take,
        CancellationToken ct = default) =>
        gates.For(repositoryPath).RunReadAsync(async token =>
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
        }, ct);

    public Task<CommitInfo?> GetFileCreatedCommitAsync(
        string repositoryPath,
        string path,
        CancellationToken ct = default) =>
        gates.For(repositoryPath).RunReadAsync(async token =>
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
        }, ct);

    public Task<IReadOnlyList<(FilePath Path, ChangeKind Kind)>> GetCommitFilesAsync(
        string repositoryPath,
        string oid,
        CancellationToken ct = default) =>
        gates.For(repositoryPath).RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                repositoryPath,
                ["show", "--name-status", "--format=", oid],
                options: null,
                token).ConfigureAwait(false);

            return (IReadOnlyList<(FilePath Path, ChangeKind Kind)>)
                GitStashService.ParseNameStatus(result.Stdout);
        }, ct);

    public Task<CommitStat> GetCommitStatAsync(
        string repositoryPath,
        string oid,
        CancellationToken ct = default) =>
        gates.For(repositoryPath).RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                repositoryPath,
                ["show", "--numstat", "--format=", oid],
                options: null,
                token).ConfigureAwait(false);

            return ParseNumstat(oid, result.Stdout);
        }, ct);

    public Task<string> GetCommitPatchAsync(
        string repositoryPath,
        string oid,
        FilePath path,
        DiffOptions options,
        CancellationToken ct = default) =>
        gates.For(repositoryPath).RunReadAsync(async token =>
        {
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

            if (options.IgnoreAllSpace)
                args.Add("-w");
            if (options.IgnoreSpaceChange)
                args.Add("--ignore-space-change");
            if (options.IgnoreBlankLines)
                args.Add("--ignore-blank-lines");

            args.Add(oid);
            args.Add("--");
            args.Add(path.Value);

            var result = await runner.RunAsync(repositoryPath, args, options: null, token)
                .ConfigureAwait(false);
            return result.Stdout;
        }, ct);

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

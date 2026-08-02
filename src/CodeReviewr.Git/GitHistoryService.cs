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

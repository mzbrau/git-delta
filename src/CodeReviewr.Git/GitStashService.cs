using System.Globalization;
using System.Text.RegularExpressions;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>Stash list, apply, push/pop, and per-file stash diffs.</summary>
public sealed class GitStashService(IGitProcessRunner runner, IRepositoryGate gate) : IGitStashService
{
    private static readonly Regex StashRefIndex = new(@"^stash@\{(\d+)\}$", RegexOptions.Compiled);

    public Task<IReadOnlyList<StashInfo>> ListStashesAsync(string repositoryPath, CancellationToken ct = default) =>
        gate.RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                repositoryPath,
                ["stash", "list", "--format=%gd%x00%s"],
                options: null,
                token).ConfigureAwait(false);

            return (IReadOnlyList<StashInfo>)ParseStashList(result.Stdout);
        }, ct);

    public Task StashPushAsync(
        string repositoryPath,
        string? message,
        bool includeUntracked = false,
        CancellationToken ct = default) =>
        gate.RunWorktreeWriteAsync(async token =>
        {
            var args = new List<string> { "stash", "push" };
            if (includeUntracked)
                args.Add("--include-untracked");
            if (!string.IsNullOrWhiteSpace(message))
            {
                args.Add("-m");
                args.Add(message);
            }

            await runner.RunAsync(repositoryPath, args, options: null, token).ConfigureAwait(false);
        }, ct);

    public Task ApplyStashAsync(string repositoryPath, int index, CancellationToken ct = default) =>
        gate.RunWorktreeWriteAsync(
            token => runner.RunAsync(
                repositoryPath,
                ["stash", "apply", $"stash@{{{index}}}"],
                options: null,
                token),
            ct);

    public Task StashPopAsync(string repositoryPath, CancellationToken ct = default) =>
        gate.RunWorktreeWriteAsync(
            token => runner.RunAsync(repositoryPath, ["stash", "pop"], options: null, token),
            ct);

    public Task<IReadOnlyList<(FilePath Path, ChangeKind Kind)>> GetStashFilesAsync(
        string repositoryPath,
        int index,
        CancellationToken ct = default) =>
        gate.RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                repositoryPath,
                ["stash", "show", "--name-status", "--format=", $"stash@{{{index}}}"],
                options: null,
                token).ConfigureAwait(false);

            return (IReadOnlyList<(FilePath Path, ChangeKind Kind)>)ParseNameStatus(result.Stdout);
        }, ct);

    public Task<string> GetStashPatchAsync(
        string repositoryPath,
        int index,
        FilePath path,
        DiffOptions options,
        CancellationToken ct = default) =>
        gate.RunReadAsync(async token =>
        {
            // stash show does not reliably accept pathspecs; diff the WIP commit against its base.
            var stashRef = $"stash@{{{index}}}";
            var args = new List<string>
            {
                "diff",
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

            args.Add($"{stashRef}^1");
            args.Add(stashRef);
            args.Add("--");
            args.Add(path.Value);

            var result = await runner.RunAsync(repositoryPath, args, options: null, token).ConfigureAwait(false);
            return result.Stdout;
        }, ct);

    public static List<StashInfo> ParseStashList(string stdout)
    {
        var list = new List<StashInfo>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = line.IndexOf('\0');
            var refPart = sep >= 0 ? line[..sep] : line;
            var message = sep >= 0 ? line[(sep + 1)..] : "";

            var match = StashRefIndex.Match(refPart.Trim());
            if (!match.Success)
                continue;

            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            string? branchHint = null;
            // Typical: "WIP on main: abc message" or "On feature: …"
            var onMatch = Regex.Match(message, @"\bon\s+([^:]+):", RegexOptions.IgnoreCase);
            if (onMatch.Success)
                branchHint = onMatch.Groups[1].Value.Trim();

            list.Add(new StashInfo(index, message, branchHint));
        }

        return list;
    }

    public static List<(FilePath Path, ChangeKind Kind)> ParseNameStatus(string stdout)
    {
        var files = new List<(FilePath Path, ChangeKind Kind)>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;

            var status = line[..tab].Trim();
            var pathPart = line[(tab + 1)..];
            // Renames: R100\told\tnew — take the new path (last field)
            var lastTab = pathPart.LastIndexOf('\t');
            if (lastTab >= 0)
                pathPart = pathPart[(lastTab + 1)..];

            if (string.IsNullOrWhiteSpace(pathPart))
                continue;

            files.Add((FilePath.From(pathPart), StatusToKind(status)));
        }

        return files;
    }

    private static ChangeKind StatusToKind(string status)
    {
        if (status.Length == 0)
            return ChangeKind.Modified;

        return status[0] switch
        {
            'A' => ChangeKind.Added,
            'D' => ChangeKind.Deleted,
            'M' => ChangeKind.Modified,
            'T' => ChangeKind.TypeChanged,
            'R' => ChangeKind.Renamed,
            'C' => ChangeKind.Copied,
            'U' => ChangeKind.Untracked,
            _ => ChangeKind.Modified,
        };
    }
}

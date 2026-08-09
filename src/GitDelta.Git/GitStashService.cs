using System.Globalization;
using System.Text.RegularExpressions;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Git.Internal;

namespace GitDelta.Git;

/// <summary>Stash list, apply, push/pop, and per-file stash diffs.</summary>
public sealed class GitStashService(
    IGitProcessRunner runner,
    IRepositoryGateProvider gates,
    ISettingsStore? settings = null) : IGitStashService
{
    private static readonly Regex StashRefIndex = new(@"^stash@\{(\d+)\}$", RegexOptions.Compiled);

    public Task<IReadOnlyList<StashInfo>> ListStashesAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                repositoryPath,
                ["stash", "list", "--format=%gd%x00%s"],
                options: null,
                token).ConfigureAwait(false);

            return (IReadOnlyList<StashInfo>)ParseStashList(result.Stdout);
        }, ct), ct);

    public Task StashPushAsync(
        string repositoryPath,
        string? message,
        bool includeUntracked = false,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunWorktreeWriteAsync(async token =>
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
        }, ct), ct);

    public Task ApplyStashAsync(string repositoryPath, int index, CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunWorktreeWriteAsync(
            token => runner.RunAsync(
                repositoryPath,
                ["stash", "apply", $"stash@{{{index}}}"],
                options: null,
                token),
            ct), ct);

    public Task StashPopAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunWorktreeWriteAsync(
            token => runner.RunAsync(repositoryPath, ["stash", "pop"], options: null, token),
            ct), ct);

    public Task DropStashAsync(string repositoryPath, int index, CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunWorktreeWriteAsync(
            token => runner.RunAsync(
                repositoryPath,
                ["stash", "drop", $"stash@{{{index}}}"],
                options: null,
                token),
            ct), ct);

    public Task<IReadOnlyList<(FilePath Path, ChangeKind Kind)>> GetStashFilesAsync(
        string repositoryPath,
        int index,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            var stashRef = $"stash@{{{index}}}";
            var result = await runner.RunAsync(
                repositoryPath,
                ["stash", "show", "--name-status", "--format=", stashRef],
                options: null,
                token).ConfigureAwait(false);

            var files = ParseNameStatus(result.Stdout);

            // Untracked files live on the third parent when the stash was created with -u.
            var untracked = await runner.RunAsync(
                repositoryPath,
                ["show", "--name-status", "--format=", "--pretty=format:", $"{stashRef}^3"],
                new GitProcessOptions { AllowNonZeroExitCode = true },
                token).ConfigureAwait(false);
            if (untracked.Succeeded)
            {
                // Files on ^3 are untracked content captured with -u.
                MergeNameStatus(
                    files,
                    ParseNameStatus(untracked.Stdout)
                        .Select(f => (f.Path, Kind: ChangeKind.Untracked))
                        .ToList());
            }

            return (IReadOnlyList<(FilePath Path, ChangeKind Kind)>)files;
        }, ct), ct);

    public Task<string> GetStashPatchAsync(
        string repositoryPath,
        int index,
        FilePath path,
        DiffOptions options,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            // stash show does not reliably accept pathspecs; diff the WIP commit against its base.
            var stashRef = $"stash@{{{index}}}";
            var args = BuildDiffArgs(options);
            args.Add($"{stashRef}^1");
            args.Add(stashRef);
            args.Add("--");
            args.Add(path.Value);

            var maxBytes = settings?.Current.MaxDiffPatchBytes ?? 32 * 1024 * 1024;
            var processOptions = new GitProcessOptions { MaxStdoutBytes = maxBytes };
            var result = await runner.RunAsync(repositoryPath, args, processOptions, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Stdout))
                return result.Stdout;

            // Untracked files are stored on the third parent.
            var untracked = await runner.RunAsync(
                repositoryPath,
                [
                    "show",
                    "--pretty=format:",
                    "--patch",
                    $"--diff-algorithm={options.Algorithm}",
                    $"-U{options.ContextLines}",
                    "--no-color",
                    "--no-ext-diff",
                    $"{stashRef}^3",
                    "--",
                    path.Value,
                ],
                new GitProcessOptions
                {
                    AllowNonZeroExitCode = true,
                    MaxStdoutBytes = maxBytes,
                },
                token).ConfigureAwait(false);
            return untracked.Succeeded ? untracked.Stdout : "";
        }, ct), ct);

    private static List<string> BuildDiffArgs(DiffOptions options)
    {
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

        return args;
    }

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

    private static void MergeNameStatus(
        List<(FilePath Path, ChangeKind Kind)> target,
        IEnumerable<(FilePath Path, ChangeKind Kind)> extra)
    {
        var seen = new HashSet<string>(target.Select(f => f.Path.Value), StringComparer.Ordinal);
        foreach (var entry in extra)
        {
            if (seen.Add(entry.Path.Value))
                target.Add(entry);
        }
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

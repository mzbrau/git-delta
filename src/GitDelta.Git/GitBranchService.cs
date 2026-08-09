using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Git.Internal;

namespace GitDelta.Git;

/// <summary>Branch listing, checkout, create/delete/rename, and fetch.</summary>
public sealed class GitBranchService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitBranchService
{
    private const string FieldSeparator = "\u0001";

    public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            var format = string.Join(
                FieldSeparator,
                "%(refname)",
                "%(HEAD)",
                "%(upstream:short)",
                "%(objectname)",
                "%(committerdate:iso-strict)");
            var result = await runner.RunAsync(
                repositoryPath,
                ["for-each-ref", "--sort=-committerdate", $"--format={format}", "refs/heads", "refs/remotes"],
                options: null,
                token).ConfigureAwait(false);

            return (IReadOnlyList<BranchInfo>)ParseBranches(result.Stdout);
        }, ct), ct);

    public Task CheckoutAsync(string repositoryPath, string branch, CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunWorktreeWriteAsync(
            token => runner.RunAsync(repositoryPath, ["checkout", branch], options: null, token),
            ct), ct);

    public Task CreateBranchAsync(string repositoryPath, string name, bool checkout, CancellationToken ct = default)
    {
        if (checkout)
        {
            return gates.WithGateAsync(repositoryPath, gate => gate.RunWorktreeWriteAsync(
                token => runner.RunAsync(repositoryPath, ["checkout", "-b", name], options: null, token),
                ct), ct);
        }

        return gates.WithGateAsync(repositoryPath, gate => gate.RunIndexWriteAsync(
            token => runner.RunAsync(repositoryPath, ["branch", "--", name], options: null, token),
            ct), ct);
    }

    public Task DeleteBranchAsync(string repositoryPath, string name, bool force, CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunIndexWriteAsync(
            token => runner.RunAsync(repositoryPath, ["branch", force ? "-D" : "-d", "--", name], options: null, token),
            ct), ct);

    public Task RenameBranchAsync(string repositoryPath, string oldName, string newName, CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunIndexWriteAsync(
            token => runner.RunAsync(repositoryPath, ["branch", "-m", "--", oldName, newName], options: null, token),
            ct), ct);

    public Task FetchAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunNetworkAsync(
            token => runner.RunAsync(
                repositoryPath,
                ["fetch", "--prune"],
                new GitProcessOptions { Timeout = TimeSpan.FromMinutes(2) },
                token),
            ct), ct);

    public Task<BranchDivergence> GetDivergenceAsync(
        string repositoryPath,
        string baseRef,
        string headRef,
        CancellationToken ct = default) =>
        gates.WithGateAsync(repositoryPath, gate => gate.RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                repositoryPath,
                ["rev-list", "--left-right", "--count", $"{baseRef}...{headRef}"],
                options: null,
                token).ConfigureAwait(false);

            return ParseDivergence(result.Stdout);
        }, ct), ct);

    internal static BranchDivergence ParseDivergence(string rawOutput)
    {
        var line = rawOutput.AsSpan().Trim();
        if (line.IsEmpty)
            return new BranchDivergence(0, 0);

        var tab = line.IndexOf('\t');
        if (tab < 0)
            tab = line.IndexOf(' ');

        if (tab < 0)
            throw new FormatException($"Unexpected rev-list --count output: '{rawOutput.Trim()}'");

        if (!int.TryParse(line[..tab], out var behind) ||
            !int.TryParse(line[(tab + 1)..], out var ahead))
        {
            throw new FormatException($"Unexpected rev-list --count output: '{rawOutput.Trim()}'");
        }

        return new BranchDivergence(behind, ahead);
    }

    internal static List<BranchInfo> ParseBranches(string rawOutput)
    {
        var branches = new List<BranchInfo>();
        foreach (var line in rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(FieldSeparator);
            if (fields.Length < 4)
                continue;

            var refName = fields[0];
            var isCurrent = fields[1] == "*";
            var upstream = string.IsNullOrEmpty(fields[2]) ? null : fields[2];
            var tipOid = fields[3];
            var tipDate = DateTimeOffset.MinValue;
            if (fields.Length >= 5
                && !string.IsNullOrWhiteSpace(fields[4])
                && DateTimeOffset.TryParse(fields[4], out var parsed))
            {
                tipDate = parsed;
            }

            var isRemote = refName.StartsWith("refs/remotes/", StringComparison.Ordinal);
            var name = isRemote
                ? refName["refs/remotes/".Length..]
                : refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? refName["refs/heads/".Length..]
                    : refName;

            branches.Add(new BranchInfo(name, isCurrent, isRemote, upstream, tipOid, tipDate));
        }

        return branches;
    }
}

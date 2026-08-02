using System.Text;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Git;

namespace CodeReviewr.Review;

internal sealed class GitReviewTree(
    string repoPath,
    CommitId commit,
    IGitProcessRunner runner,
    IRepositoryGateProvider gates) : IReviewTree
{
    public string? MaterialisedPath => null;

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(FilePath path, CancellationToken ct)
    {
        var bytes = await gates.For(repoPath).RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                    repoPath,
                    ["show", $"{commit.Value}:{path.Value}"],
                    options: null,
                    token)
                .ConfigureAwait(false);

            if (!result.Succeeded)
                throw new GitException($"git show {commit.Value}:{path.Value} failed: {result.Stderr}");

            return Encoding.UTF8.GetBytes(result.Stdout);
        }, ct).ConfigureAwait(false);

        return bytes.AsMemory();
    }

    public async ValueTask<IReadOnlyList<FilePath>> ListAsync(FilePath prefix, CancellationToken ct)
    {
        return await gates.For(repoPath).RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                    repoPath,
                    ["ls-tree", "-r", "--name-only", commit.Value],
                    options: null,
                    token)
                .ConfigureAwait(false);

            if (!result.Succeeded)
                throw new GitException($"git ls-tree failed: {result.Stderr}");

            var prefixValue = prefix.Value;
            var hasPrefix = prefixValue.Length > 0;
            var paths = new List<FilePath>();
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (hasPrefix &&
                    !trimmed.StartsWith(prefixValue, StringComparison.Ordinal) &&
                    !string.Equals(trimmed, prefixValue, StringComparison.Ordinal))
                    continue;

                paths.Add(FilePath.From(trimmed));
            }

            return (IReadOnlyList<FilePath>)paths;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string pattern, CancellationToken ct)
    {
        return await gates.For(repoPath).RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                    repoPath,
                    ["grep", "-n", "--no-color", pattern, commit.Value],
                    options: null,
                    token)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                if (result.ExitCode == 1)
                    return (IReadOnlyList<SearchHit>)Array.Empty<SearchHit>();
                throw new GitException($"git grep failed: {result.Stderr}");
            }

            var hits = new List<SearchHit>();
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parsed = ParseGrepLine(line);
                if (parsed is not null)
                    hits.Add(parsed.Value);
            }

            return (IReadOnlyList<SearchHit>)hits;
        }, ct).ConfigureAwait(false);
    }

    private static SearchHit? ParseGrepLine(string line)
    {
        // path:line:text  OR  commit:path:line:text
        var segments = line.Split(':');
        if (segments.Length < 3)
            return null;

        int pathIndex;
        int lineIndex;
        int textIndex;
        if (segments.Length >= 4 && segments[1].Contains('/'))
        {
            pathIndex = 1;
            lineIndex = 2;
            textIndex = 3;
        }
        else
        {
            pathIndex = 0;
            lineIndex = 1;
            textIndex = 2;
        }

        if (!int.TryParse(segments[lineIndex], out var lineNumber))
            return null;

        var path = segments[pathIndex];
        var lineText = string.Join(':', segments.Skip(textIndex));
        return new SearchHit(FilePath.From(path), lineNumber, lineText);
    }
}

internal sealed class GitReviewTreeFactory(
    IGitProcessRunner runner,
    IRepositoryGateProvider gates) : IReviewTreeFactory
{
    public IReviewTree Create(string repoPath, CommitId commit) =>
        new GitReviewTree(repoPath, commit, runner, gates);
}

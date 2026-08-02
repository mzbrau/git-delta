using System.Diagnostics;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>
/// Stages and unstages content. Partial staging goes through `git apply --cached`, inheriting
/// Git's exact patch semantics (including clean/smudge filters such as Git LFS) instead of
/// reimplementing them. Hunk/line subset synthesis lives in `CodeReviewr.Diff`; this service
/// only applies whatever patch text it is given.
/// </summary>
public sealed class GitStagingService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitStagingService
{
    public Task StageFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default) =>
        StageFilesAsync(repositoryPath, [path], ct);

    public Task UnstageFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default) =>
        UnstageFilesAsync(repositoryPath, [path], ct);

    public Task StageFilesAsync(string repositoryPath, IReadOnlyList<FilePath> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        var args = new List<string>(2 + paths.Count) { "add", "--" };
        foreach (var p in paths)
            args.Add(p.Value);
        return RunIndexWrite(repositoryPath, args, stdin: null, ct);
    }

    public Task UnstageFilesAsync(string repositoryPath, IReadOnlyList<FilePath> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        var args = new List<string>(2 + paths.Count) { "reset", "--" };
        foreach (var p in paths)
            args.Add(p.Value);
        return RunIndexWrite(repositoryPath, args, stdin: null, ct);
    }

    public Task StagePatchAsync(string repositoryPath, string patch, CancellationToken ct = default) =>
        RunIndexWrite(repositoryPath, ["apply", "--cached", "--whitespace=nowarn", "-"], patch, ct);

    public Task UnstagePatchAsync(string repositoryPath, string patch, CancellationToken ct = default) =>
        RunIndexWrite(repositoryPath, ["apply", "--cached", "--reverse", "--whitespace=nowarn", "-"], patch, ct);

    private Task RunIndexWrite(string repositoryPath, IReadOnlyList<string> args, string? stdin, CancellationToken ct) =>
        gates.For(repositoryPath).RunIndexWriteAsync(async token =>
        {
            var sw = Stopwatch.StartNew();
            var options = stdin is null ? null : new GitProcessOptions { StdinText = stdin };
            await runner.RunAsync(repositoryPath, args, options, token).ConfigureAwait(false);
            CodeReviewrMeters.StageMs.Record(sw.Elapsed.TotalMilliseconds);
        }, ct);
}

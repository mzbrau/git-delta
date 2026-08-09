using System.Diagnostics;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diagnostics;
using GitDelta.Git.Internal;

namespace GitDelta.Git;

/// <summary>
/// Stages and unstages content. Partial staging goes through `git apply --cached`, inheriting
/// Git's exact patch semantics (including clean/smudge filters such as Git LFS) instead of
/// reimplementing them. Hunk/line subset synthesis lives in `GitDelta.Diff`; this service
/// only applies whatever patch text it is given.
/// </summary>
public sealed class GitStagingService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitStagingService
{
    public Task StageFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default, bool force = false) =>
        StageFilesAsync(repositoryPath, [path], ct, force);

    public Task UnstageFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default) =>
        UnstageFilesAsync(repositoryPath, [path], ct);

    public Task StageFilesAsync(string repositoryPath, IReadOnlyList<FilePath> paths, CancellationToken ct = default, bool force = false)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        var args = new List<string>(3 + paths.Count) { "add" };
        if (force)
            args.Add("-f");
        args.Add("--");
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
        gates.WithGateAsync(repositoryPath, gate => gate.RunIndexWriteAsync(async token =>
        {
            using var activity = GitDeltaActivity.Source.StartActivity("git.stage");
            activity?.SetTag("git.command", args.Count > 0 ? args[0] : "");
            var sw = Stopwatch.StartNew();
            try
            {
                var options = stdin is null ? null : new GitProcessOptions { StdinText = stdin };
                await runner.RunAsync(repositoryPath, args, options, token).ConfigureAwait(false);
            }
            finally
            {
                GitDeltaMeters.StageMs.Record(sw.Elapsed.TotalMilliseconds);
            }
        }, ct), ct);
}

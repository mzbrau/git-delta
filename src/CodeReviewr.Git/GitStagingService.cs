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
public sealed class GitStagingService(IGitProcessRunner runner, IRepositoryGate gate) : IGitStagingService
{
    public Task StageFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default) =>
        RunIndexWrite(repositoryPath, ["add", "--", path.Value], stdin: null, ct);

    public Task UnstageFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default) =>
        RunIndexWrite(repositoryPath, ["reset", "--", path.Value], stdin: null, ct);

    public Task StagePatchAsync(string repositoryPath, string patch, CancellationToken ct = default) =>
        RunIndexWrite(repositoryPath, ["apply", "--cached", "--whitespace=nowarn", "-"], patch, ct);

    public Task UnstagePatchAsync(string repositoryPath, string patch, CancellationToken ct = default) =>
        RunIndexWrite(repositoryPath, ["apply", "--cached", "--reverse", "--whitespace=nowarn", "-"], patch, ct);

    private Task RunIndexWrite(string repositoryPath, IReadOnlyList<string> args, string? stdin, CancellationToken ct) =>
        gate.RunIndexWriteAsync(async token =>
        {
            var sw = Stopwatch.StartNew();
            var options = stdin is null ? null : new GitProcessOptions { StdinText = stdin };
            await runner.RunAsync(repositoryPath, args, options, token).ConfigureAwait(false);
            CodeReviewrMeters.StageMs.Record(sw.Elapsed.TotalMilliseconds);
        }, ct);
}

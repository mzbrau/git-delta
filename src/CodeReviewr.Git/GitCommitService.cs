using System.Diagnostics;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>
/// Commits. Hooks run under the CLI backend: `pre-commit` can fail, take tens of seconds, and
/// modify staged files, so commit is a progress-bearing operation rather than an instant one.
/// Post-commit state is never predicted, unlike stage/unstage.
/// </summary>
public sealed class GitCommitService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitCommitService
{
    public Task CommitAsync(
        string repositoryPath,
        string message,
        bool amend,
        bool noVerify,
        IProgress<string>? hookOutput,
        CancellationToken ct = default)
    {
        var args = new List<string> { "commit" };
        if (amend)
            args.Add("--amend");
        if (noVerify)
            args.Add("--no-verify");
        args.Add("-F");
        args.Add("-");

        return gates.For(repositoryPath).RunIndexWriteAsync(async token =>
        {
            var sw = Stopwatch.StartNew();
            var options = new GitProcessOptions
            {
                StdinText = message,
                OnStdoutLine = line => hookOutput?.Report(line),
                OnStderrLine = line => hookOutput?.Report(line),
            };

            await runner.RunAsync(repositoryPath, args, options, token).ConfigureAwait(false);
            CodeReviewrMeters.CommitMs.Record(sw.Elapsed.TotalMilliseconds);
        }, ct);
    }

    public Task CherryPickAsync(string repositoryPath, string oid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oid);
        return gates.For(repositoryPath).RunWorktreeWriteAsync(
            token => runner.RunAsync(repositoryPath, ["cherry-pick", oid], options: null, token),
            ct);
    }

    public Task RevertAsync(string repositoryPath, string oid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oid);
        return gates.For(repositoryPath).RunWorktreeWriteAsync(
            token => runner.RunAsync(repositoryPath, ["revert", "--no-edit", oid], options: null, token),
            ct);
    }
}

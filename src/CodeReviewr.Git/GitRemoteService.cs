using System.Diagnostics;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>
/// Push and pull. Push touches neither the index nor the worktree, so it holds no repository
/// gate and runs single-flight through the network gate instead — a slow push must never block
/// a diff read. Pull is a worktree write: `--ff-only` is the default mode, merge and rebase are
/// explicit opt-ins.
/// </summary>
public sealed class GitRemoteService(IGitProcessRunner runner, IRepositoryGate gate) : IGitRemoteService
{
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);

    public Task PushAsync(string repositoryPath, IProgress<string>? progress, CancellationToken ct = default) =>
        gate.RunNetworkAsync(async token =>
        {
            var sw = Stopwatch.StartNew();
            var options = new GitProcessOptions
            {
                Timeout = NetworkTimeout,
                OnStdoutLine = line => progress?.Report(line),
                OnStderrLine = line => progress?.Report(line),
            };
            await runner.RunAsync(repositoryPath, ["push", "--progress"], options, token).ConfigureAwait(false);
            CodeReviewrMeters.PushMs.Record(sw.Elapsed.TotalMilliseconds);
        }, ct);

    public Task PullAsync(string repositoryPath, PullMode mode, IProgress<string>? progress, CancellationToken ct = default) =>
        gate.RunWorktreeWriteAsync(async token =>
        {
            var args = new List<string> { "pull", "--progress" };
            args.Add(mode switch
            {
                PullMode.FfOnly => "--ff-only",
                PullMode.Merge => "--no-rebase",
                PullMode.Rebase => "--rebase",
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            });

            var sw = Stopwatch.StartNew();
            var options = new GitProcessOptions
            {
                Timeout = NetworkTimeout,
                OnStdoutLine = line => progress?.Report(line),
                OnStderrLine = line => progress?.Report(line),
            };
            await runner.RunAsync(repositoryPath, args, options, token).ConfigureAwait(false);
            CodeReviewrMeters.PullMs.Record(sw.Elapsed.TotalMilliseconds);
        }, ct);

    public Task<string?> GetRemoteUrlAsync(
        string repositoryPath,
        string remoteName = "origin",
        CancellationToken ct = default) =>
        gate.RunReadAsync(async token =>
        {
            try
            {
                var result = await runner.RunAsync(
                    repositoryPath,
                    ["remote", "get-url", remoteName],
                    options: null,
                    token).ConfigureAwait(false);
                var url = result.Stdout.Trim();
                return string.IsNullOrEmpty(url) ? null : url;
            }
            catch (GitException)
            {
                return null;
            }
        }, ct);
}

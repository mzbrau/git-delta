using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using GitDelta.Git;
using GitDelta.GitHub;

namespace GitDelta.Review;

public sealed class ReviewService(
    IPullRequestService pullRequestService,
    IPullRequestGitService pullRequestGit,
    IGitDiffService diffService,
    IReviewTreeFactory reviewTreeFactory,
    LocalRepositoryLocator localRepositoryLocator,
    ISettingsStore settingsStore,
    IGitProcessRunner runner,
    IRepositoryGateProvider gates) : IReviewService
{
    public async Task<ReviewSession> OpenAsync(PullRequestSummary summary, CancellationToken ct = default)
    {
        var locate = await localRepositoryLocator.LocateAsync(
                summary.Host, summary.Owner, summary.Name, ct)
            .ConfigureAwait(false);

        if (!locate.Found)
        {
            var settings = settingsStore.Current;
            var cloneUrl = LocalRepositoryLocator.BuildCloneUrl(summary.Host, summary.Owner, summary.Name);
            var suggestedPath = LocalRepositoryLocator.BuildSuggestedPath(settings, summary.Owner, summary.Name);
            throw new LocalCloneRequiredException(
                summary.Host, summary.Owner, summary.Name, cloneUrl, suggestedPath);
        }

        var repoPath = locate.LocalPath!;
        var detail = await pullRequestService.GetPullRequestAsync(
                summary.Host,
                summary.AccountLogin,
                summary.Owner,
                summary.Name,
                summary.Number,
                ct)
            .ConfigureAwait(false);

        var remote = await localRepositoryLocator.ResolveRemoteAsync(repoPath, ct).ConfigureAwait(false)
                     ?? LocalRepositoryLocator.BuildCloneUrl(summary.Host, summary.Owner, summary.Name);

        await pullRequestGit.FetchPullRequestHeadAsync(repoPath, remote, summary.Number, ct)
            .ConfigureAwait(false);

        var headRef = PullRequestGitService.LocalRefName(summary.Number);
        var head = CommitId.FromSha(await RevParseAsync(repoPath, headRef, ct).ConfigureAwait(false));

        CommitId mergeBase;
        if (!string.IsNullOrWhiteSpace(detail.Summary.BaseOid))
        {
            mergeBase = CommitId.FromSha(detail.Summary.BaseOid);
        }
        else if (!string.IsNullOrWhiteSpace(summary.BaseOid))
        {
            mergeBase = CommitId.FromSha(summary.BaseOid);
        }
        else
        {
            var baseRef = $"origin/{detail.Summary.BaseRefName}";
            var baseOid = await RevParseAsync(repoPath, baseRef, ct).ConfigureAwait(false);
            mergeBase = await pullRequestGit.ResolveMergeBaseAsync(
                    repoPath,
                    CommitId.FromSha(baseOid),
                    head,
                    ct)
                .ConfigureAwait(false);
        }

        var scope = new DiffScope.Revisions(mergeBase, head);
        var rawFiles = await diffService.GetRawDiffAsync(repoPath, scope, DiffOptions.Default, ct)
            .ConfigureAwait(false);
        var files = rawFiles
            .Select(f => (f.Path, f.Kind))
            .ToList();

        var headTree = reviewTreeFactory.Create(repoPath, head);
        return new ReviewSession(repoPath, detail, mergeBase, head, headTree, files);
    }

    public Task<FileDiff> GetDiffAsync(
        ReviewSession session,
        FilePath path,
        DiffOptions options,
        CancellationToken ct = default)
    {
        var scope = new DiffScope.Revisions(session.MergeBase, session.Head);
        return diffService.GetDiffAsync(session.RepositoryPath, path, scope, options, ct);
    }

    private Task<string> RevParseAsync(string repoPath, string refName, CancellationToken ct) =>
        gates.WithGateAsync(repoPath, gate => gate.RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(
                    repoPath,
                    ["rev-parse", refName],
                    options: null,
                    token)
                .ConfigureAwait(false);

            if (!result.Succeeded)
                throw new GitException($"git rev-parse {refName} failed: {result.Stderr}");

            var sha = result.Stdout.Trim();
            if (sha.Length == 0)
                throw new GitException($"git rev-parse {refName} returned empty output");

            return sha;
        }, ct), ct);
}

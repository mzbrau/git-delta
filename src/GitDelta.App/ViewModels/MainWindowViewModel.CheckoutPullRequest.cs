using CommunityToolkit.Mvvm.Input;
using GitDelta.App.Services;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.GitHub;
using GitDelta.Review;

namespace GitDelta.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly LocalRepositoryLocator _repositoryLocatorForPr;
    private readonly IGitStatusService _statusForPrCheckout;
    private readonly IGitCloneService _cloneService;
    private readonly ICheckoutPullRequestDialog _checkoutPrDialog;

    [RelayCommand(CanExecute = nameof(CanCheckoutPullRequestBranch))]
    private async Task CheckoutPullRequestBranchAsync()
    {
        var summary = Review.SelectedPullRequest;
        if (summary is null)
            return;

        var branchName = summary.HeadRefName;
        if (string.IsNullOrWhiteSpace(branchName))
        {
            _notifications.Error("This pull request has no head branch name.");
            return;
        }

        try
        {
            var path = await ResolveClonePathAsync(summary).ConfigureAwait(false);
            if (path is null)
                return;

            var candidates = await BuildCandidatesAsync(summary, path).ConfigureAwait(false);
            if (candidates.Count == 0)
                return;

            var result = await _checkoutPrDialog.ShowAsync(new CheckoutPullRequestDialogModel(
                    summary.Title,
                    branchName,
                    summary.NameWithOwner,
                    candidates))
                .ConfigureAwait(false);

            if (!result.Confirmed || string.IsNullOrWhiteSpace(result.SelectedPath))
                return;

            PersistRepositoryBinding(summary, result.SelectedPath);

            await OpenRepositoryPathAsync(result.SelectedPath).ConfigureAwait(false);
            Review.ClearPullRequestMode();
            WorkingCopy.SelectFileStatusCommand.Execute(null);

            await WorkingCopy.CheckoutRemoteBranchAsync(branchName).ConfigureAwait(false);
            await EnsureRepositoryCatalogAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Checkout branch failed: {ex.Message}", exception: ex);
        }
    }

    private bool CanCheckoutPullRequestBranch() =>
        Review.IsPullRequestMode && Review.SelectedPullRequest is not null;

    private async Task<string?> ResolveClonePathAsync(PullRequestSummary summary)
    {
        var locate = await _repositoryLocatorForPr
            .LocateAsync(summary.Host, summary.Owner, summary.Name)
            .ConfigureAwait(false);

        if (locate.Found && !string.IsNullOrWhiteSpace(locate.LocalPath))
            return locate.LocalPath;

        if (locate.Ambiguous && locate.Candidates is { Count: > 0 })
            return locate.Candidates[0]; // dialog will list all candidates

        var cloneUrl = LocalRepositoryLocator.BuildCloneUrl(summary.Host, summary.Owner, summary.Name);
        var suggested = LocalRepositoryLocator.BuildSuggestedPath(_settings.Current, summary.Owner, summary.Name);
        var confirmed = await _confirm.ConfirmAsync(
                "Clone repository?",
                $"No local clone was found for {summary.NameWithOwner}. Clone into the development folder before checking out `{summary.HeadRefName}`?",
                "Clone")
            .ConfigureAwait(false);
        if (!confirmed)
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(suggested)!);
        await _cloneService.CloneAsync(cloneUrl, suggested, progress: null).ConfigureAwait(false);
        return suggested;
    }

    private async Task<IReadOnlyList<CheckoutPullRequestCandidate>> BuildCandidatesAsync(
        PullRequestSummary summary,
        string preferredPath)
    {
        var locate = await _repositoryLocatorForPr
            .LocateAsync(summary.Host, summary.Owner, summary.Name)
            .ConfigureAwait(false);

        List<string> paths = [];
        if (locate.Ambiguous && locate.Candidates is { Count: > 0 })
            paths.AddRange(locate.Candidates);
        else if (locate.Found && locate.LocalPath is not null)
            paths.Add(locate.LocalPath);
        else if (Directory.Exists(preferredPath))
            paths.Add(preferredPath);

        paths = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToList();

        var current = WorkingCopy.RepositoryPath;
        var candidates = new List<CheckoutPullRequestCandidate>();
        foreach (var path in paths)
        {
            var statusSummary = await FormatWorkingTreeStatusAsync(path).ConfigureAwait(false);
            candidates.Add(new CheckoutPullRequestCandidate(
                path,
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                statusSummary,
                IsCurrentRepository: !string.IsNullOrEmpty(current)
                    && string.Equals(current, path, StringComparison.OrdinalIgnoreCase)));
        }

        return candidates;
    }

    private async Task<string> FormatWorkingTreeStatusAsync(string path)
    {
        try
        {
            var status = await _statusForPrCheckout.GetStatusAsync(path).ConfigureAwait(false);
            var branch = string.IsNullOrEmpty(status.CurrentBranch) ? "detached" : status.CurrentBranch;
            if (status.Conflicted.Count > 0)
                return $"{status.Conflicted.Count} conflicted · {branch}";

            var staged = status.Staged.Count;
            var unstaged = status.Unstaged.Count;
            if (staged == 0 && unstaged == 0)
                return $"Clean · {branch}";

            return $"{staged} staged, {unstaged} unstaged · {branch}";
        }
        catch (Exception ex)
        {
            return $"Status unavailable ({ex.Message})";
        }
    }

    private void PersistRepositoryBinding(PullRequestSummary summary, string localPath)
    {
        var host = GitHubClient.NormalizeHost(summary.Host);
        _settings.Update(s =>
        {
            s.RepositoryBindings.RemoveAll(b =>
                string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(b.Owner, summary.Owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(b.Name, summary.Name, StringComparison.OrdinalIgnoreCase));
            s.RepositoryBindings.Add(new RepositoryAccountBinding
            {
                Host = host,
                Owner = summary.Owner,
                Name = summary.Name,
                LocalPath = localPath,
                AccountLogin = summary.AccountLogin,
            });
        });
        _ = _settings.SaveAsync();
    }
}

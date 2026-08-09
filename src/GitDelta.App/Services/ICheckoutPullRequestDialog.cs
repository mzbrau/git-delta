namespace GitDelta.App.Services;

public sealed record CheckoutPullRequestCandidate(
    string Path,
    string DisplayName,
    string StatusSummary,
    bool IsCurrentRepository);

public sealed record CheckoutPullRequestDialogModel(
    string PullRequestTitle,
    string BranchName,
    string NameWithOwner,
    IReadOnlyList<CheckoutPullRequestCandidate> Candidates);

public sealed record CheckoutPullRequestDialogResult(bool Confirmed, string? SelectedPath);

/// <summary>Confirms checking out a PR head branch, optionally choosing among local clones.</summary>
public interface ICheckoutPullRequestDialog
{
    Task<CheckoutPullRequestDialogResult> ShowAsync(CheckoutPullRequestDialogModel model);
}

/// <summary>Test helper that confirms the first (or only) candidate.</summary>
public sealed class AlwaysConfirmCheckoutPullRequestDialog : ICheckoutPullRequestDialog
{
    public Task<CheckoutPullRequestDialogResult> ShowAsync(CheckoutPullRequestDialogModel model)
    {
        var path = model.Candidates.FirstOrDefault()?.Path;
        return Task.FromResult(new CheckoutPullRequestDialogResult(path is not null, path));
    }
}

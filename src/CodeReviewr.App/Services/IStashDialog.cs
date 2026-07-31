namespace CodeReviewr.App.Services;

public enum StashDialogAction
{
    Push,
    Pop,
}

public sealed record StashDialogResult(
    StashDialogAction Action,
    string? Message,
    bool IncludeUntracked);

/// <summary>Prompts the user to push or pop a stash.</summary>
public interface IStashDialog
{
    /// <returns>The chosen action, or <c>null</c> if the user cancelled.</returns>
    Task<StashDialogResult?> ShowAsync();
}

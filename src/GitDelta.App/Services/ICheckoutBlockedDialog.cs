namespace GitDelta.App.Services;

public enum CheckoutBlockedChoice
{
    Cancel,
    StashOnly,
    StashAndRestore,
}

/// <summary>Prompts when checkout is blocked by local changes.</summary>
public interface ICheckoutBlockedDialog
{
    /// <summary>
    /// Asks whether to stash (and optionally restore after checkout) so the target ref can be checked out.
    /// </summary>
    Task<CheckoutBlockedChoice> ShowAsync(string targetRef);
}

/// <summary>Test / fallback dialog that always cancels.</summary>
public sealed class CancelCheckoutBlockedDialog : ICheckoutBlockedDialog
{
    public static CancelCheckoutBlockedDialog Instance { get; } = new();

    public Task<CheckoutBlockedChoice> ShowAsync(string targetRef) =>
        Task.FromResult(CheckoutBlockedChoice.Cancel);
}

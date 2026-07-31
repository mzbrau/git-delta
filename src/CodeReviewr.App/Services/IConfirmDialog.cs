namespace CodeReviewr.App.Services;

/// <summary>Prompts the user to confirm a destructive action.</summary>
public interface IConfirmDialog
{
    /// <returns><c>true</c> if the user confirmed; otherwise <c>false</c>.</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Discard");
}

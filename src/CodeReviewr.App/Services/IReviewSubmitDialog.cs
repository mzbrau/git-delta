namespace CodeReviewr.App.Services;

/// <summary>Prompts for an optional review summary before Approve / Request changes.</summary>
public interface IReviewSubmitDialog
{
    /// <returns>The summary body (possibly empty), or <c>null</c> if cancelled.</returns>
    Task<string?> ShowAsync(string title, string confirmLabel);
}

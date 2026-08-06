namespace GitDelta.Git;

/// <summary>
/// Heuristic classification of `git` stderr text into actionable failure categories.
/// Locale is pinned to <c>LC_ALL=C</c> by <see cref="GitProcessRunner"/> so these markers are stable.
/// </summary>
internal static class GitErrorClassifier
{
    private static readonly string[] AuthFailureMarkers =
    [
        "authentication failed",
        "permission denied (publickey)",
        "permission denied, please try again",
        "could not read username",
        "could not read password",
        "terminal prompts disabled",
        "invalid username or password",
        "support for password authentication was removed",
        "fatal: could not read from remote repository",
        "access denied",
        "403",
    ];

    private static readonly string[] IndexLockExistsMarkers =
    [
        "unable to create",
        "file exists",
    ];

    public static bool IsAuthFailure(string stderr) =>
        !string.IsNullOrEmpty(stderr) &&
        Array.Exists(AuthFailureMarkers, marker => stderr.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static bool IsIndexLocked(string stderr)
    {
        if (string.IsNullOrEmpty(stderr) || !stderr.Contains("index.lock", StringComparison.OrdinalIgnoreCase))
            return false;

        return Array.Exists(IndexLockExistsMarkers, marker => stderr.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

namespace CodeReviewr.Core;

/// <summary>
/// Resolves repository-relative paths to absolute worktree paths and rejects path traversal.
/// </summary>
public static class RepositoryPathResolver
{
    /// <summary>
    /// Combines <paramref name="repositoryRoot"/> with a repo-relative <paramref name="relativePath"/>
    /// and returns the full path only when it stays under the repository root.
    /// </summary>
    /// <exception cref="InvalidOperationException">When the resolved path escapes the repository root.</exception>
    public static string ResolveUnderRoot(string repositoryRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var root = Path.GetFullPath(repositoryRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        var combined = Path.GetFullPath(
            Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)
            && !string.Equals(
                combined.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Path '{relativePath}' resolves outside the repository root.");
        }

        return combined;
    }

    public static string ResolveUnderRoot(string repositoryRoot, FilePath path) =>
        ResolveUnderRoot(repositoryRoot, path.Value);
}

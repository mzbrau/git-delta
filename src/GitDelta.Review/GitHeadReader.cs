namespace GitDelta.Review;

/// <summary>
/// Best-effort current-branch read from <c>.git/HEAD</c> without spawning git.
/// </summary>
public static class GitHeadReader
{
    /// <summary>
    /// Returns the branch name for an attached HEAD, a short SHA for detached HEAD, or null on failure.
    /// </summary>
    public static string? TryReadCurrentBranch(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            return null;

        try
        {
            var gitDir = ResolveGitDir(repositoryPath);
            var headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath))
                return null;

            return ParseHeadContent(File.ReadAllText(headPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses the contents of a git HEAD file.</summary>
    public static string? ParseHeadContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var trimmed = content.Trim();
        const string headsPrefix = "ref: refs/heads/";
        if (trimmed.StartsWith(headsPrefix, StringComparison.Ordinal))
        {
            var branch = trimmed[headsPrefix.Length..];
            return string.IsNullOrWhiteSpace(branch) ? null : branch;
        }

        if (trimmed.StartsWith("ref: ", StringComparison.Ordinal))
        {
            var refName = trimmed["ref: ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(refName))
                return null;
            var slash = refName.LastIndexOf('/');
            return slash >= 0 && slash < refName.Length - 1
                ? refName[(slash + 1)..]
                : refName;
        }

        // Detached HEAD — object name.
        return trimmed.Length >= 7 ? trimmed[..7] : trimmed;
    }

    private static string ResolveGitDir(string repositoryPath)
    {
        var dotGit = Path.Combine(repositoryPath, ".git");

        if (Directory.Exists(dotGit))
            return dotGit;

        if (File.Exists(dotGit))
        {
            var content = File.ReadAllText(dotGit).Trim();
            const string prefix = "gitdir:";
            if (content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var pointedPath = content[prefix.Length..].Trim();
                return Path.IsPathRooted(pointedPath)
                    ? pointedPath
                    : Path.GetFullPath(Path.Combine(repositoryPath, pointedPath));
            }
        }

        return dotGit;
    }
}

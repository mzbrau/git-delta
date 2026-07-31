using CodeReviewr.Core;

namespace CodeReviewr.Git.Internal;

/// <summary>
/// Resolves the real `.git` directory (handling worktrees and submodules, where `.git` is a
/// file containing `gitdir: <path>`) and detects in-progress operations from filesystem markers.
/// </summary>
internal static class GitRepositoryPaths
{
    public static string ResolveGitDir(string repositoryPath)
    {
        var dotGit = Path.Combine(repositoryPath, ".git");

        if (Directory.Exists(dotGit))
            return dotGit;

        if (File.Exists(dotGit))
        {
            var content = File.ReadAllText(dotGit).Trim();
            const string prefix = "gitdir:";
            if (content.StartsWith(prefix, StringComparison.Ordinal))
            {
                var pointedPath = content[prefix.Length..].Trim();
                return Path.IsPathRooted(pointedPath)
                    ? pointedPath
                    : Path.GetFullPath(Path.Combine(repositoryPath, pointedPath));
            }
        }

        return dotGit;
    }

    /// <summary>
    /// Detects an in-progress merge/rebase/cherry-pick/revert purely from the filesystem, so a
    /// paused rebase with a clean index is still reported honestly.
    /// </summary>
    public static InProgressOperation DetectInProgress(string repositoryPath)
    {
        var gitDir = ResolveGitDir(repositoryPath);

        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
            return InProgressOperation.Merge;

        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
            Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            return InProgressOperation.Rebase;

        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            return InProgressOperation.CherryPick;

        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
            return InProgressOperation.Revert;

        return InProgressOperation.None;
    }
}

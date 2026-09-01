using GitDelta.Core;

namespace GitDelta.Git.Internal;

/// <summary>Parses <c>git worktree list --porcelain</c> output.</summary>
internal static class WorktreeListParser
{
    public static IReadOnlyList<WorktreeEntry> Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return [];

        var raw = new List<RawWorktree>();
        string? path = null;
        string? head = null;
        string? branch = null;
        var detached = false;
        var bare = false;
        var locked = false;
        var prunable = false;

        void Flush()
        {
            if (path is null)
                return;

            raw.Add(new RawWorktree(
                NormalizePath(path),
                head ?? "",
                branch,
                detached,
                bare,
                locked,
                prunable));
            path = null;
            head = null;
            branch = null;
            detached = false;
            bare = false;
            locked = false;
            prunable = false;
        }

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            if (trimmed.StartsWith("worktree ", StringComparison.Ordinal))
                path = trimmed["worktree ".Length..];
            else if (trimmed.StartsWith("HEAD ", StringComparison.Ordinal))
                head = trimmed["HEAD ".Length..];
            else if (trimmed.StartsWith("branch ", StringComparison.Ordinal))
                branch = ParseBranchRef(trimmed["branch ".Length..]);
            else if (trimmed == "detached")
                detached = true;
            else if (trimmed == "bare")
                bare = true;
            else if (trimmed == "locked" || trimmed.StartsWith("locked ", StringComparison.Ordinal))
                locked = true;
            else if (trimmed == "prunable")
                prunable = true;
        }

        Flush();

        var result = new List<WorktreeEntry>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var item = raw[i];
            result.Add(new WorktreeEntry(
                item.Path,
                item.HeadSha,
                item.BranchName,
                IsMain: i == 0,
                item.IsDetached,
                item.IsBare,
                item.IsLocked,
                item.IsPrunable));
        }

        return result;
    }

    private static string? ParseBranchRef(string refName)
    {
        refName = refName.Trim();
        if (string.IsNullOrWhiteSpace(refName))
            return null;

        const string headsPrefix = "refs/heads/";
        if (refName.StartsWith(headsPrefix, StringComparison.Ordinal))
        {
            var branch = refName[headsPrefix.Length..];
            return string.IsNullOrWhiteSpace(branch) ? null : branch;
        }

        if (refName.StartsWith("refs/", StringComparison.Ordinal))
        {
            var slash = refName.LastIndexOf('/');
            return slash >= 0 && slash < refName.Length - 1
                ? refName[(slash + 1)..]
                : refName;
        }

        return refName;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            return path;
        }
    }

    private sealed record RawWorktree(
        string Path,
        string HeadSha,
        string? BranchName,
        bool IsDetached,
        bool IsBare,
        bool IsLocked,
        bool IsPrunable);
}

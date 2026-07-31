using CodeReviewr.Core;

namespace CodeReviewr.Git.Internal;

/// <summary>
/// Builds `git diff` argument lists. `git diff` is the single source of truth for file-level
/// structure and hunk boundaries; whitespace and rename/copy settings map directly onto Git's
/// own flags rather than being reimplemented.
/// </summary>
internal static class GitDiffArgumentBuilder
{
    public static List<string> BuildPatchArgs(DiffTarget target, DiffOptions options, FilePath? path)
    {
        var args = BuildCommonArgs(target, options);
        args.Add("--no-color");
        args.Add("--no-ext-diff");
        AppendPathspec(args, path);
        return args;
    }

    public static List<string> BuildRawArgs(DiffTarget target, DiffOptions options, FilePath? path)
    {
        var args = BuildCommonArgs(target, options);
        args.Add("--raw");
        args.Add("-z");
        AppendPathspec(args, path);
        return args;
    }

    private static List<string> BuildCommonArgs(DiffTarget target, DiffOptions options)
    {
        var args = new List<string> { "diff" };

        switch (target)
        {
            case DiffTarget.IndexToWorktree:
                break;
            case DiffTarget.HeadToIndex:
                args.Add("--cached");
                break;
            case DiffTarget.HeadToWorktree:
                args.Add("HEAD");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }

        args.Add($"--diff-algorithm={options.Algorithm}");
        args.Add($"-U{options.ContextLines}");

        if (options.DetectRenames)
            args.Add("-M");
        if (options.DetectCopies)
            args.Add("-C");
        if (options.IgnoreAllSpace)
            args.Add("-w");
        if (options.IgnoreSpaceChange)
            args.Add("--ignore-space-change");
        if (options.IgnoreBlankLines)
            args.Add("--ignore-blank-lines");

        return args;
    }

    private static void AppendPathspec(List<string> args, FilePath? path)
    {
        args.Add("--");
        if (path is { } p)
            args.Add(p.Value);
    }
}

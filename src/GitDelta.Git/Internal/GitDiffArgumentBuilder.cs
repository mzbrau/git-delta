using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.Git.Internal;

/// <summary>
/// Builds `git diff` argument lists. `git diff` is the single source of truth for file-level
/// structure and hunk boundaries; whitespace and rename/copy settings map directly onto Git's
/// own flags rather than being reimplemented.
/// </summary>
internal static class GitDiffArgumentBuilder
{
    public static List<string> BuildPatchArgs(DiffScope scope, DiffOptions options, FilePath? path)
    {
        var args = BuildCommonArgs(scope, options);
        args.Add("--no-color");
        args.Add("--no-ext-diff");
        AppendPathspec(args, path);
        return args;
    }

    public static List<string> BuildRawArgs(DiffScope scope, DiffOptions options, FilePath? path)
    {
        var args = BuildCommonArgs(scope, options);
        args.Add("--raw");
        args.Add("-z");
        AppendPathspec(args, path);
        return args;
    }

    public static List<string> BuildPatchArgs(DiffTarget target, DiffOptions options, FilePath? path) =>
        BuildPatchArgs(target.AsWorkingCopy(), options, path);

    public static List<string> BuildRawArgs(DiffTarget target, DiffOptions options, FilePath? path) =>
        BuildRawArgs(target.AsWorkingCopy(), options, path);

    private static List<string> BuildCommonArgs(DiffScope scope, DiffOptions options)
    {
        var args = new List<string> { "diff" };

        switch (scope)
        {
            case DiffScope.WorkingCopy wc:
                switch (wc.Target)
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
                        throw new ArgumentOutOfRangeException(nameof(scope), wc.Target, null);
                }

                break;
            case DiffScope.Revisions rev:
                args.Add($"{rev.Base.Value}...{rev.Head.Value}");
                break;
            case DiffScope.RevisionsTwoDot rev:
                args.Add(rev.Base.Value);
                args.Add(rev.Head.Value);
                break;
            case DiffScope.RevisionToWorktree rev:
                args.Add(rev.Revision.Value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, null);
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

using CodeReviewr.Core;

namespace CodeReviewr.Git;

/// <summary>Structured result of a single `git` invocation. Never shown to the user verbatim.</summary>
public sealed record GitCommandResult(int ExitCode, string Stdout, string Stderr, bool IsAuthFailure, bool IsIndexLocked)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Builds an actionable exception from a failed result. Stderr is summarised, not surfaced raw.</summary>
    public GitException ToException(IReadOnlyList<string> arguments)
    {
        var summary = string.IsNullOrWhiteSpace(Stderr) ? null : Stderr.Trim();

        var message = IsAuthFailure
            ? "Git authentication failed. Check your credential helper (Git Credential Manager, osxkeychain) or, for SSH, that your agent is running with the right key loaded."
            : IsIndexLocked
                ? "Another Git process is holding the repository lock (.git/index.lock). Close any other Git client or terminal operation and try again."
                : $"git {string.Join(' ', arguments)} failed with exit code {ExitCode}.";

        return new GitException(message, ExitCode, summary, IsAuthFailure, IsIndexLocked);
    }
}


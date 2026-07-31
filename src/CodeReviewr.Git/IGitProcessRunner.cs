namespace CodeReviewr.Git;

/// <summary>Hardened `git` process invocation. See <see cref="GitProcessRunner"/> for the implementation contract.</summary>
public interface IGitProcessRunner
{
    /// <summary>The executable path or bare command name currently used to launch `git`.</summary>
    string ExecutablePath { get; }

    /// <summary>Updates the executable path used for subsequent invocations. Called by <see cref="GitEnvironment"/> once detection succeeds.</summary>
    void SetExecutablePath(string executablePath);

    /// <summary>
    /// Runs a single `git` invocation to completion and returns a structured result.
    /// Never routes through a shell. Always applies the hardened environment and `-c core.quotePath=false`.
    /// </summary>
    Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitProcessOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Starts a long-lived `git` process (e.g. `cat-file --batch`) with duplex stdin/stdout streams
    /// the caller drives directly. The caller owns request/response framing.
    /// </summary>
    ILongLivedGitProcess StartLongLived(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default);
}

/// <summary>A running long-lived `git` process with streams the caller can read/write incrementally.</summary>
public interface ILongLivedGitProcess : IAsyncDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Task<int> Completion { get; }
    bool HasExited { get; }
}

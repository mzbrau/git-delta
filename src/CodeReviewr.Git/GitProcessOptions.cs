using CliWrap;

namespace CodeReviewr.Git;

/// <summary>Per-invocation options for <see cref="IGitProcessRunner"/>.</summary>
public sealed class GitProcessOptions
{
    public static readonly GitProcessOptions Default = new();

    /// <summary>Text piped to the process's stdin, e.g. a patch for `apply --cached` or a message for `commit -F -`.</summary>
    public string? StdinText { get; init; }

    /// <summary>Invoked for each stdout line as it streams in, in addition to any buffering.</summary>
    public Action<string>? OnStdoutLine { get; init; }

    /// <summary>Invoked for each stderr line as it streams in, in addition to any buffering. Used for hook/progress output.</summary>
    public Action<string>? OnStderrLine { get; init; }

    /// <summary>
    /// When set, stdout is routed to this target instead of being buffered into <see cref="GitCommandResult.Stdout"/>.
    /// Use for genuinely large outputs (e.g. blob content) that must not be materialised as a single string.
    /// </summary>
    public PipeTarget? StdoutTarget { get; init; }

    /// <summary>
    /// When set, the process is cancelled and a <see cref="CodeReviewr.Core.DiffTooLargeException"/> is thrown
    /// once buffered/streamed stdout exceeds this many bytes.
    /// </summary>
    public long? MaxStdoutBytes { get; init; }

    /// <summary>Hard timeout, primarily for network operations that could otherwise hang indefinitely.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>When false (default), a non-zero exit code throws a <see cref="GitException"/>.</summary>
    public bool AllowNonZeroExitCode { get; init; }

    /// <summary>Overrides the configured executable path for a single call. Used by <see cref="GitEnvironment"/> during detection.</summary>
    public string? ExecutableOverride { get; init; }

    /// <summary>
    /// Extra environment variables merged over the runner defaults (e.g. <c>GIT_INDEX_FILE</c>
    /// for a temporary index when snapshotting the working copy).
    /// </summary>
    public IReadOnlyDictionary<string, string?>? ExtraEnvironment { get; init; }
}

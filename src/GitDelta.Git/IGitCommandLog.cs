namespace GitDelta.Git;

/// <summary>Captures raw git invocations for the in-app console.</summary>
public interface IGitCommandLog
{
    event EventHandler? Changed;

    IReadOnlyList<GitCommandLogEntry> Entries { get; }

    void Append(GitCommandLogEntry entry);

    void Clear();
}

public sealed record GitCommandLogEntry(
    DateTimeOffset Timestamp,
    string WorkingDirectory,
    string CommandLine,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool IsLongLivedStart = false);

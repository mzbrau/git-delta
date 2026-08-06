using GitDelta.Core.Abstractions;

namespace GitDelta.Git;

/// <summary>
/// Clones a repository. Freely killable: nothing in an existing repository changes, and a
/// cancelled clone removes the partial target directory rather than leaving debris behind.
/// Clone predates the target repository, so it needs no <see cref="IRepositoryGate"/>.
/// </summary>
public sealed class GitCloneService(IGitProcessRunner runner) : IGitCloneService
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(30);

    public async Task CloneAsync(string url, string targetDirectory, IProgress<string>? progress, CancellationToken ct = default)
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(targetDirectory)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(parentDirectory);

        var options = new GitProcessOptions
        {
            Timeout = CloneTimeout,
            OnStdoutLine = line => progress?.Report(line),
            OnStderrLine = line => progress?.Report(line),
        };

        try
        {
            await runner.RunAsync(parentDirectory, ["clone", "--progress", "--", url, targetDirectory], options, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDeletePartialClone(targetDirectory);
            throw;
        }
    }

    private static void TryDeletePartialClone(string targetDirectory)
    {
        try
        {
            if (Directory.Exists(targetDirectory))
                Directory.Delete(targetDirectory, recursive: true);
        }
        catch
        {
            // Best effort: leaving a partial directory behind is preferable to masking the original failure.
        }
    }
}

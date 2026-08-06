using System.Collections.Concurrent;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Git.Internal;

namespace GitDelta.Git;

/// <summary>
/// Discards worktree edits. Discard is categorically different from every other Git operation
/// here: staged content can be unstaged, a bad checkout can be checked out again, but a
/// discarded worktree edit is gone because Git never had it.
///
/// Recoverable by construction: before the destructive step, the pre-image is written into the
/// object database with `git hash-object -w`, and its path/object id is kept in a bounded
/// recently-discarded list with <see cref="RestoreDiscardedAsync"/>. Untracked files work
/// identically — their content is hashed before deletion.
/// </summary>
public sealed class GitDiscardService(IGitProcessRunner runner, IGitObjectReader objectReader, IRepositoryGateProvider gates) : IGitDiscardService
{
    private const int MaxRecentlyDiscarded = 20;
    private readonly ConcurrentQueue<DiscardedEntry> _recentlyDiscarded = new();

    public IReadOnlyList<DiscardedEntry> RecentlyDiscarded => [.. _recentlyDiscarded];

    public async Task DiscardFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default)
    {
        var absolutePath = Path.Combine(repositoryPath, path.Value);
        if (!File.Exists(absolutePath))
            return;

        var preImage = await objectReader.HashObjectAsync(repositoryPath, absolutePath, write: true, ct).ConfigureAwait(false);
        var isTracked = await IsTrackedAsync(repositoryPath, path, ct).ConfigureAwait(false);

        await gates.For(repositoryPath).RunWorktreeWriteAsync(async token =>
        {
            if (isTracked)
            {
                await runner.RunAsync(repositoryPath, ["restore", "--", path.Value], options: null, token).ConfigureAwait(false);
            }
            else
            {
                File.Delete(absolutePath);
            }
        }, ct).ConfigureAwait(false);

        RecordDiscarded(path, preImage, wasUntracked: !isTracked);
    }

    public async Task DiscardStagedFileAsync(string repositoryPath, FilePath path, CancellationToken ct = default)
    {
        var absolutePath = Path.Combine(repositoryPath, path.Value);
        ContentId? preImage = File.Exists(absolutePath)
            ? await objectReader.HashObjectAsync(repositoryPath, absolutePath, write: true, ct).ConfigureAwait(false)
            : null;

        await gates.For(repositoryPath).RunWorktreeWriteAsync(async token =>
        {
            await runner.RunAsync(
                repositoryPath,
                ["restore", "--source=HEAD", "--staged", "--worktree", "--", path.Value],
                options: null,
                token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (preImage is { } oid)
            RecordDiscarded(path, oid, wasUntracked: false);
    }

    public async Task DiscardPatchAsync(string repositoryPath, string patch, CancellationToken ct = default)
    {
        var path = ExtractPathFromPatch(patch)
            ?? throw new GitException("Could not determine the target file from the patch.");

        var absolutePath = Path.Combine(repositoryPath, path.Value);
        ContentId? preImage = File.Exists(absolutePath)
            ? await objectReader.HashObjectAsync(repositoryPath, absolutePath, write: true, ct).ConfigureAwait(false)
            : null;

        await gates.For(repositoryPath).RunWorktreeWriteAsync(async token =>
        {
            var options = new GitProcessOptions { StdinText = patch };
            await runner.RunAsync(repositoryPath, ["apply", "--reverse", "--whitespace=nowarn", "-"], options, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (preImage is { } oid)
            RecordDiscarded(path, oid, wasUntracked: false);
    }

    public async Task RestoreDiscardedAsync(string repositoryPath, DiscardedEntry entry, CancellationToken ct = default)
    {
        var content = await objectReader.ReadBlobAsync(repositoryPath, entry.ObjectId, ct).ConfigureAwait(false);

        await gates.For(repositoryPath).RunWorktreeWriteAsync(async _ =>
        {
            var absolutePath = Path.Combine(repositoryPath, entry.Path.Value);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllBytesAsync(absolutePath, content, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        RemoveFromRecentlyDiscarded(entry);
    }

    private async Task<bool> IsTrackedAsync(string repositoryPath, FilePath path, CancellationToken ct)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            ["ls-files", "--error-unmatch", "--", path.Value],
            new GitProcessOptions { AllowNonZeroExitCode = true },
            ct).ConfigureAwait(false);

        return result.Succeeded;
    }

    private void RecordDiscarded(FilePath path, ContentId objectId, bool wasUntracked)
    {
        _recentlyDiscarded.Enqueue(new DiscardedEntry(path, objectId, DateTimeOffset.UtcNow, wasUntracked));
        while (_recentlyDiscarded.Count > MaxRecentlyDiscarded && _recentlyDiscarded.TryDequeue(out _))
        {
        }
    }

    private void RemoveFromRecentlyDiscarded(DiscardedEntry entry)
    {
        var remaining = _recentlyDiscarded.Where(e => e != entry).ToArray();
        _recentlyDiscarded.Clear();
        foreach (var item in remaining)
            _recentlyDiscarded.Enqueue(item);
    }

    /// <summary>Extracts the target path from a unified diff's `+++ b/&lt;path&gt;` (or `--- a/&lt;path&gt;` for deletions) header line.</summary>
    private static FilePath? ExtractPathFromPatch(string patch)
    {
        foreach (var rawLine in patch.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
                return FilePath.From(line["+++ b/".Length..]);
            if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal) && TryExtractOldPath(patch, out var oldPath))
                return oldPath;
        }

        return null;
    }

    private static bool TryExtractOldPath(string patch, out FilePath? path)
    {
        foreach (var rawLine in patch.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("--- a/", StringComparison.Ordinal))
            {
                path = FilePath.From(line["--- a/".Length..]);
                return true;
            }
        }

        path = null;
        return false;
    }
}

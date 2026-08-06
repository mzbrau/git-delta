using System.Formats.Tar;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Git;
using GitDelta.Review;
using Microsoft.Extensions.Logging;

namespace GitDelta.AI;

/// <summary>Result of materialising a commit's tree to disk for the agent's <c>cwd</c>.</summary>
public sealed record MaterialisationResult(
    IReviewTree Tree,
    bool WasCacheHit,
    IReadOnlyList<string> MissingExportIgnorePaths)
{
    public string Path => Tree.MaterialisedPath!;
}

/// <summary>
/// Exports a pinned commit's tree to <c>%AppData%/GitDelta/trees/&lt;sha&gt;/</c> via
/// <c>git archive</c> so the agent gets a real filesystem <c>cwd</c> to work in, instead of one
/// synthetic `git show` round-trip per file read. Exports are content-addressed by SHA and reused
/// across runs/PRs that share a commit.
/// </summary>
public sealed class ReviewTreeMaterialiser(
    IGitProcessRunner runner,
    IRepositoryGateProvider gates,
    IReviewTreeFactory reviewTreeFactory,
    ILogger<ReviewTreeMaterialiser> logger)
{
    private static string RootDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitDelta",
        "trees");

    public async Task<MaterialisationResult> MaterialiseAsync(
        string repositoryPath,
        string sha,
        CancellationToken ct = default)
    {
        var innerTree = reviewTreeFactory.Create(repositoryPath, new CommitId(sha));
        var targetDirectory = System.IO.Path.Combine(RootDirectory, SanitiseSha(sha));

        var wasCacheHit = Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any();
        if (wasCacheHit)
        {
            TouchDirectory(targetDirectory);
        }
        else
        {
            Directory.CreateDirectory(targetDirectory);
            try
            {
                await ExportAsync(repositoryPath, sha, targetDirectory, ct).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteDirectory(targetDirectory);
                throw;
            }
        }

        var missing = await FindMissingAsync(targetDirectory, innerTree, ct).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            logger.LogWarning(
                "Materialised tree for {Sha} is missing {Count} path(s) known to Git (likely export-ignore attributes).",
                sha,
                missing.Count);
        }

        var tree = new MaterialisedReviewTree(innerTree, targetDirectory);
        return new MaterialisationResult(tree, wasCacheHit, missing);
    }

    /// <summary>Deletes materialised exports whose directory has not been touched in over <paramref name="retentionDays"/> days.</summary>
    public Task CleanupUnusedAsync(int retentionDays, CancellationToken ct = default)
    {
        if (!Directory.Exists(RootDirectory))
            return Task.CompletedTask;

        var cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(Math.Max(0, retentionDays));
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.GetLastWriteTimeUtc(directory) < cutoffUtc)
                TryDeleteDirectory(directory);
        }

        return Task.CompletedTask;
    }

    public Task ClearAllExportsAsync(CancellationToken ct = default)
    {
        TryDeleteDirectory(RootDirectory);
        return Task.CompletedTask;
    }

    private async Task ExportAsync(string repositoryPath, string sha, string targetDirectory, CancellationToken ct)
    {
        var tarPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gitdelta-tree-{Guid.NewGuid():N}.tar");
        try
        {
            await gates.For(repositoryPath).RunReadAsync(async token =>
            {
                var result = await runner.RunAsync(
                        repositoryPath,
                        ["archive", "--format=tar", sha],
                        new GitProcessOptions { StdoutFilePath = tarPath },
                        token)
                    .ConfigureAwait(false);

                if (!result.Succeeded)
                    throw new GitException($"git archive --format=tar {sha} failed: {result.Stderr}");

                return true;
            }, ct).ConfigureAwait(false);

            await TarFile.ExtractToDirectoryAsync(tarPath, targetDirectory, overwriteFiles: true, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tarPath);
        }
    }

    private static async Task<IReadOnlyList<string>> FindMissingAsync(
        string targetDirectory,
        IReviewTree sourceTree,
        CancellationToken ct)
    {
        var expected = await sourceTree.ListAsync(FilePath.From(""), ct).ConfigureAwait(false);
        var missing = new List<string>();
        foreach (var path in expected)
        {
            var fullPath = System.IO.Path.Combine(targetDirectory, path.Value.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                missing.Add(path.Value);
        }

        return missing;
    }

    private static void TouchDirectory(string path)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Best-effort; a stale timestamp only affects retention cleanup, not correctness.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static string SanitiseSha(string sha) =>
        new([.. sha.Where(char.IsLetterOrDigit)]);
}

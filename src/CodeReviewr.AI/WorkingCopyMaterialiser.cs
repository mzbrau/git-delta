using System.Formats.Tar;
using CliWrap;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.AI;
using CodeReviewr.Git;
using CodeReviewr.Review;
using Microsoft.Extensions.Logging;

namespace CodeReviewr.AI;

/// <summary>
/// Snapshots staged or all pending changes into a content-addressed tree export for the agent cwd.
/// Never points the agent at the live worktree.
/// </summary>
public sealed class WorkingCopyMaterialiser(
    IGitProcessRunner runner,
    IRepositoryGateProvider gates,
    ILogger<WorkingCopyMaterialiser> logger)
{
    private static string RootDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodeReviewr",
        "trees");

    /// <summary>
    /// Builds a temporary index for the given scope and returns the resulting tree OID
    /// (suitable as <see cref="AiReviewRequest.HeadSha"/> for working-copy reviews).
    /// </summary>
    public async Task<string> WriteTreeAsync(
        string repositoryPath,
        AiReviewScope scope,
        CancellationToken ct = default)
    {
        if (scope is not (AiReviewScope.WorkingCopyStaged or AiReviewScope.WorkingCopyAll))
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Expected a working-copy scope.");

        return await gates.For(repositoryPath).RunReadAsync(async token =>
        {
            if (scope == AiReviewScope.WorkingCopyStaged)
            {
                var staged = await runner.RunAsync(repositoryPath, ["write-tree"], options: null, token)
                    .ConfigureAwait(false);
                if (!staged.Succeeded)
                    throw new GitException($"git write-tree failed: {staged.Stderr}");
                return staged.Stdout.Trim();
            }

            var indexPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"codereviewr-index-{Guid.NewGuid():N}");
            try
            {
                var env = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = indexPath };
                var options = new GitProcessOptions { ExtraEnvironment = env };

                var readTree = await runner.RunAsync(
                        repositoryPath, ["read-tree", "HEAD"], options, token)
                    .ConfigureAwait(false);
                if (!readTree.Succeeded)
                    throw new GitException($"git read-tree HEAD failed: {readTree.Stderr}");

                var add = await runner.RunAsync(
                        repositoryPath, ["add", "-A", "--"], options, token)
                    .ConfigureAwait(false);
                if (!add.Succeeded)
                    throw new GitException($"git add -A failed: {add.Stderr}");

                var writeTree = await runner.RunAsync(
                        repositoryPath, ["write-tree"], options, token)
                    .ConfigureAwait(false);
                if (!writeTree.Succeeded)
                    throw new GitException($"git write-tree (temp index) failed: {writeTree.Stderr}");

                return writeTree.Stdout.Trim();
            }
            finally
            {
                TryDeleteFile(indexPath);
                TryDeleteFile(indexPath + ".lock");
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Exports a tree OID to the shared content-addressed trees cache.</summary>
    public async Task<MaterialisationResult> MaterialiseAsync(
        string repositoryPath,
        string treeOid,
        CancellationToken ct = default)
    {
        var targetDirectory = System.IO.Path.Combine(RootDirectory, SanitiseSha(treeOid));
        var wasCacheHit = Directory.Exists(targetDirectory) &&
                          Directory.EnumerateFileSystemEntries(targetDirectory).Any();

        if (wasCacheHit)
        {
            TouchDirectory(targetDirectory);
        }
        else
        {
            Directory.CreateDirectory(targetDirectory);
            try
            {
                await ExportAsync(repositoryPath, treeOid, targetDirectory, ct).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteDirectory(targetDirectory);
                throw;
            }
        }

        IReviewTree tree = new FilesystemReviewTree(targetDirectory);
        return new MaterialisationResult(tree, wasCacheHit, MissingExportIgnorePaths: []);
    }

    /// <summary>Write-tree for the scope, then materialise the export. Returns tree OID + result.</summary>
    public async Task<(string TreeOid, MaterialisationResult Result)> SnapshotAndMaterialiseAsync(
        string repositoryPath,
        AiReviewScope scope,
        CancellationToken ct = default)
    {
        var treeOid = await WriteTreeAsync(repositoryPath, scope, ct).ConfigureAwait(false);
        var result = await MaterialiseAsync(repositoryPath, treeOid, ct).ConfigureAwait(false);
        logger.LogDebug(
            "Working-copy materialisation for {Scope} produced tree {TreeOid} (cacheHit={CacheHit})",
            scope,
            treeOid,
            result.WasCacheHit);
        return (treeOid, result);
    }

    private async Task ExportAsync(string repositoryPath, string treeOid, string targetDirectory, CancellationToken ct)
    {
        var tarPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codereviewr-tree-{Guid.NewGuid():N}.tar");
        try
        {
            await gates.For(repositoryPath).RunReadAsync(async token =>
            {
                var result = await runner.RunAsync(
                        repositoryPath,
                        ["archive", "--format=tar", treeOid],
                        new GitProcessOptions { StdoutTarget = PipeTarget.ToFile(tarPath) },
                        token)
                    .ConfigureAwait(false);

                if (!result.Succeeded)
                    throw new GitException($"git archive --format=tar {treeOid} failed: {result.Stderr}");

                return true;
            }, ct).ConfigureAwait(false);

            await TarFile.ExtractToDirectoryAsync(tarPath, targetDirectory, overwriteFiles: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tarPath);
        }
    }

    private static void TouchDirectory(string path)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
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
        }
    }

    private static string SanitiseSha(string sha) =>
        new([.. sha.Where(char.IsLetterOrDigit)]);
}

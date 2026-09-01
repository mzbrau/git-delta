using GitDelta.Core;
using GitDelta.Core.Abstractions;

namespace GitDelta.Review;

public sealed class RepositoryLocator(
    ISettingsStore settingsStore,
    IGitRemoteService gitRemoteService,
    IGitWorktreeService gitWorktreeService) : IRepositoryLocator
{
    public async IAsyncEnumerable<LocatedRepository> ScanAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var settings = settingsStore.Current;
        var root = settings.DevelopmentFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            yield break;

        var ignore = new HashSet<string>(
            settings.RepositoryScanIgnore,
            StringComparer.OrdinalIgnoreCase);
        var maxDepth = Math.Max(1, settings.RepositoryScanDepth);

        await foreach (var repoPath in ScanDirectoryAsync(root, ignore, maxDepth, ct).ConfigureAwait(false))
        {
            string? remoteUrl = null;
            string? host = null;
            string? owner = null;
            string? name = null;

            try
            {
                remoteUrl = await gitRemoteService.GetRemoteUrlAsync(repoPath, ct: ct).ConfigureAwait(false);
                if (remoteUrl is not null &&
                    RemoteUrlHelper.TryParse(remoteUrl, out var parsedHost, out var parsedOwner, out var parsedName))
                {
                    host = parsedHost;
                    owner = parsedOwner;
                    name = parsedName;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Remote lookup is best-effort during scan.
            }

            yield return new LocatedRepository(repoPath, host, owner, name, remoteUrl);
        }
    }

    public async IAsyncEnumerable<LocatedRepository> ScanLocalAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var settings = settingsStore.Current;
        var root = settings.DevelopmentFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            yield break;

        var ignore = new HashSet<string>(
            settings.RepositoryScanIgnore,
            StringComparer.OrdinalIgnoreCase);
        var maxDepth = Math.Max(1, settings.RepositoryScanDepth);

        await foreach (var repoPath in ScanDirectoryAsync(root, ignore, maxDepth, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var branch = GitHeadReader.TryReadCurrentBranch(repoPath);
            yield return new LocatedRepository(repoPath, null, null, null, null, branch);
        }
    }

    public async IAsyncEnumerable<LocatedRepository> ScanCatalogAsync(
        IEnumerable<string>? extraSeeds = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var catalog = new Dictionary<string, LocatedRepository>(StringComparer.OrdinalIgnoreCase);
        var seedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedCommonDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var located in ScanLocalAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            AddCatalogEntry(catalog, seedPaths, located);
        }

        if (extraSeeds is not null)
        {
            foreach (var seed in extraSeeds)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(seed))
                    continue;

                var normalized = NormalizeCatalogPath(seed);
                if (!Directory.Exists(normalized) || !IsGitRepository(normalized))
                    continue;

                seedPaths.Add(normalized);
                if (catalog.ContainsKey(normalized))
                    continue;

                var branch = GitHeadReader.TryReadCurrentBranch(normalized);
                AddCatalogEntry(
                    catalog,
                    seedPaths,
                    new LocatedRepository(normalized, null, null, null, null, branch));
            }
        }

        foreach (var seedPath in seedPaths.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(seedPath) || !IsGitRepository(seedPath))
                continue;

            string? commonDir;
            try
            {
                commonDir = await gitWorktreeService.TryGetCommonDirectoryAsync(seedPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(commonDir) || !processedCommonDirs.Add(commonDir))
                continue;

            IReadOnlyList<WorktreeEntry> worktrees;
            try
            {
                worktrees = await gitWorktreeService.ListWorktreesAsync(seedPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (worktrees.Count == 0)
                continue;

            var mainPath = worktrees[0].Path;
            foreach (var worktree in worktrees)
            {
                if (!Directory.Exists(worktree.Path))
                    continue;

                var branch = worktree.BranchName ?? GitHeadReader.TryReadCurrentBranch(worktree.Path);
                AddCatalogEntry(
                    catalog,
                    seedPaths,
                    new LocatedRepository(
                        worktree.Path,
                        null,
                        null,
                        null,
                        null,
                        branch,
                        IsLinkedWorktree: !worktree.IsMain,
                        MainWorktreePath: worktree.IsMain ? null : mainPath));
            }
        }

        foreach (var entry in catalog.Values.OrderBy(e => e.LocalPath, StringComparer.OrdinalIgnoreCase))
            yield return entry;
    }

    private static void AddCatalogEntry(
        Dictionary<string, LocatedRepository> catalog,
        HashSet<string> seedPaths,
        LocatedRepository entry)
    {
        var normalized = NormalizeCatalogPath(entry.LocalPath);
        seedPaths.Add(normalized);
        catalog[normalized] = entry with { LocalPath = normalized };
    }

    private static string NormalizeCatalogPath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            if (Directory.Exists(trimmed))
                return new DirectoryInfo(trimmed).FullName.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static async IAsyncEnumerable<string> ScanDirectoryAsync(
        string root,
        HashSet<string> ignore,
        int maxDepth,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (current, depth) = pending.Dequeue();

            if (IsGitRepository(current))
            {
                yield return NormalizeCatalogPath(current);
                continue;
            }

            if (depth >= maxDepth)
                continue;

            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var subdir in subdirs)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();

                var dirName = Path.GetFileName(subdir);
                if (dirName.StartsWith('.') || ignore.Contains(dirName))
                    continue;

                pending.Enqueue((subdir, depth + 1));
            }
        }
    }

    private static bool IsGitRepository(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }
}

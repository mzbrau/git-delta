using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.Review;

public sealed class RepositoryLocator(
    ISettingsStore settingsStore,
    IGitRemoteService gitRemoteService) : IRepositoryLocator
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
                yield return current;
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

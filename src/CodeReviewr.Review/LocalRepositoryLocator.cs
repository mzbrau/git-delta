using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.GitHub;

namespace CodeReviewr.Review;

public sealed class LocalRepositoryLocator(
    IRepositoryLocator repositoryLocator,
    ISettingsStore settingsStore,
    IGitRemoteService gitRemoteService)
{
    public async Task<LocateResult> LocateAsync(
        string host,
        string owner,
        string name,
        CancellationToken ct = default)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);
        var settings = settingsStore.Current;

        var binding = settings.RepositoryBindings.FirstOrDefault(b =>
            string.Equals(b.Host, normalizedHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(b.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

        if (binding is not null && Directory.Exists(binding.LocalPath))
            return new LocateResult(Found: true, binding.LocalPath, Ambiguous: false, Candidates: null);

        var matches = new List<string>();
        await foreach (var located in repositoryLocator.ScanAsync(ct).ConfigureAwait(false))
        {
            if (located.Host is null || located.Owner is null || located.Name is null)
                continue;

            if (!string.Equals(located.Host, normalizedHost, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(located.Owner, owner, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(located.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            matches.Add(located.LocalPath);
        }

        if (matches.Count == 1)
            return new LocateResult(Found: true, matches[0], Ambiguous: false, Candidates: null);

        if (matches.Count > 1)
        {
            if (binding is not null && matches.Contains(binding.LocalPath, StringComparer.OrdinalIgnoreCase))
                return new LocateResult(Found: true, binding.LocalPath, Ambiguous: false, Candidates: null);

            return new LocateResult(Found: false, null, Ambiguous: true, matches);
        }

        return new LocateResult(Found: false, null, Ambiguous: false, Candidates: null);
    }

    public async Task<string?> ResolveRemoteAsync(string repoPath, CancellationToken ct = default)
    {
        try
        {
            return await gitRemoteService.GetRemoteUrlAsync(repoPath, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static string BuildCloneUrl(string host, string owner, string name)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);
        if (string.Equals(normalizedHost, "github.com", StringComparison.OrdinalIgnoreCase))
            return $"https://github.com/{owner}/{name}.git";

        return $"https://{normalizedHost}/{owner}/{name}.git";
    }

    public static string BuildSuggestedPath(AppSettings settings, string owner, string name)
    {
        var root = settings.DevelopmentFolder;
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Development");

        return Path.Combine(root, owner, name);
    }
}

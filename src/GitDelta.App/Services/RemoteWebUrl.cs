using System.Text.RegularExpressions;

namespace GitDelta.App.Services;

/// <summary>Converts git remote clone URLs into browser URLs for GitHub and Bitbucket.</summary>
public static class RemoteWebUrl
{
    private static readonly Regex ScpLike =
        new(@"^git@(?<host>[^:]+):(?<path>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SshUri =
        new(@"^ssh://(?:git@)?(?<host>[^/]+)/(?<path>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns an https browse URL, or null when the remote cannot be opened in a browser.
    /// </summary>
    public static string? ToBrowseUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;

        var url = remoteUrl.Trim();

        if (ScpLike.Match(url) is { Success: true } scp)
            return BuildHttps(scp.Groups["host"].Value, scp.Groups["path"].Value);

        if (SshUri.Match(url) is { Success: true } ssh)
            return BuildHttps(ssh.Groups["host"].Value, ssh.Groups["path"].Value);

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var path = uri.AbsolutePath.TrimStart('/');
            return BuildHttps(uri.Host, path, uri.Scheme);
        }

        return null;
    }

    private static string BuildHttps(string host, string path, string scheme = "https")
    {
        path = path.Trim().TrimStart('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        host = host.Trim().ToLowerInvariant();
        // Normalize known hosts (strip port if present for scp-style we already have host only)
        return $"{scheme}://{host}/{path}";
    }
}

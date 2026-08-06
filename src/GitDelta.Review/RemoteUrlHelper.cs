using System.Text.RegularExpressions;

namespace GitDelta.Review;

public static partial class RemoteUrlHelper
{
    [GeneratedRegex(@"^git@([^:/]+):([^/]+)/(.+?)(?:\.git)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpStyleRegex();

    [GeneratedRegex(@"^ssh://git@([^/]+)/([^/]+)/(.+?)(?:\.git)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SshUrlRegex();

    [GeneratedRegex(@"^https?://([^/]+)/([^/]+)/(.+?)(?:\.git)?/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HttpsUrlRegex();

    /// <summary>Parses a Git remote URL into host, owner, and repository name.</summary>
    public static bool TryParse(string? remoteUrl, out string host, out string owner, out string name)
    {
        host = owner = name = string.Empty;
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return false;

        var trimmed = remoteUrl.Trim();

        Match? match = ScpStyleRegex().Match(trimmed);
        if (!match.Success)
            match = SshUrlRegex().Match(trimmed);
        if (!match.Success)
            match = HttpsUrlRegex().Match(trimmed);
        if (!match.Success)
            return false;

        host = NormalizeHost(match.Groups[1].Value);
        owner = match.Groups[2].Value;
        name = match.Groups[3].Value.TrimEnd('/');
        return owner.Length > 0 && name.Length > 0;
    }

    internal static string NormalizeHost(string host)
    {
        var trimmed = host.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("https://", StringComparison.Ordinal))
            trimmed = trimmed["https://".Length..];
        else if (trimmed.StartsWith("http://", StringComparison.Ordinal))
            trimmed = trimmed["http://".Length..];
        return trimmed.TrimEnd('/');
    }
}

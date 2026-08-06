using System.Text.Json;

namespace GitDelta.GitHub;

public sealed record GitHubViewer(string Login, string AvatarUrl);

public sealed record GitHubRateLimit(int Limit, int Remaining, int Used, DateTimeOffset? ResetAt);

public sealed record GitHubCapabilities(bool MarkFileAsViewed);

public interface IGitHubClient
{
    Task<GitHubViewer> GetViewerAsync(string host, string token, CancellationToken ct = default);
    Task<GitHubCapabilities> ProbeCapabilitiesAsync(string host, string token, CancellationToken ct = default);
    Task<(JsonElement Data, GitHubRateLimit? RateLimit)> ExecuteAsync(
        string host, string token, string query, object? variables, CancellationToken ct = default);
}

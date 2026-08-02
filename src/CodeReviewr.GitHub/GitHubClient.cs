using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeReviewr.GitHub;

public sealed class GitHubClient(HttpClient httpClient, ICapabilityCache capabilityCache) : IGitHubClient
{
    public async Task<GitHubViewer> GetViewerAsync(string host, string token, CancellationToken ct = default)
    {
        var (data, _) = await ExecuteAsync(host, token, EmbeddedQueries.ViewerQuery, null, ct)
            .ConfigureAwait(false);

        var viewer = data.GetProperty("viewer");
        var login = viewer.GetProperty("login").GetString()
            ?? throw new InvalidOperationException("Viewer login missing from response.");
        var avatarUrl = viewer.GetProperty("avatarUrl").GetString()
            ?? throw new InvalidOperationException("Viewer avatarUrl missing from response.");

        return new GitHubViewer(login, avatarUrl);
    }

    public async Task<GitHubCapabilities> ProbeCapabilitiesAsync(
        string host, string token, CancellationToken ct = default)
    {
        try
        {
            var normalizedHost = NormalizeHost(host);
            var login = await ResolveLoginAsync(normalizedHost, token, ct).ConfigureAwait(false);
            var cacheKey = new CapabilityCacheKey(normalizedHost, login);

            if (capabilityCache.TryGet(cacheKey, out var cached))
                return cached;

            var (data, _) = await ExecuteAsync(
                    normalizedHost, token, EmbeddedQueries.CapabilityProbeQuery, null, ct)
                .ConfigureAwait(false);

            var markFileAsViewed = data
                .GetProperty("__type")
                .GetProperty("fields")
                .EnumerateArray()
                .Any(f => string.Equals(
                    f.GetProperty("name").GetString(), "markFileAsViewed", StringComparison.Ordinal));

            var capabilities = new GitHubCapabilities(markFileAsViewed);
            capabilityCache.Set(cacheKey, capabilities);
            return capabilities;
        }
        catch
        {
            return new GitHubCapabilities(MarkFileAsViewed: false);
        }
    }

    public async Task<(JsonElement Data, GitHubRateLimit? RateLimit)> ExecuteAsync(
        string host, string token, string query, object? variables, CancellationToken ct = default)
    {
        var endpoint = ResolveGraphQlEndpoint(host);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CodeReviewr");

        var payload = variables is null
            ? new { query }
            : (object)new { query, variables };
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException(
                (int)response.StatusCode,
                $"GitHub API request failed with status {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var msg)
                ? msg.GetString()
                : "GraphQL request failed.";
            throw new InvalidOperationException(message ?? "GraphQL request failed.");
        }

        var data = doc.RootElement.GetProperty("data").Clone();
        var rateLimit = ParseRateLimit(doc.RootElement, response);
        return (data, rateLimit);
    }

    public static string ResolveGraphQlEndpoint(string host)
    {
        var normalized = NormalizeHost(host);
        return normalized == "github.com"
            ? "https://api.github.com/graphql"
            : $"https://{normalized}/api/graphql";
    }

    public static string NormalizeHost(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["https://".Length..];
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["http://".Length..];

        return trimmed.TrimEnd('/').ToLowerInvariant();
    }

    private async Task<string> ResolveLoginAsync(string host, string token, CancellationToken ct)
    {
        var viewer = await GetViewerAsync(host, token, ct).ConfigureAwait(false);
        return viewer.Login;
    }

    private static GitHubRateLimit? ParseRateLimit(JsonElement root, HttpResponseMessage response)
    {
        if (root.TryGetProperty("extensions", out var extensions) &&
            extensions.TryGetProperty("rateLimit", out var rateLimitElement))
        {
            return ParseRateLimitElement(rateLimitElement);
        }

        if (!response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues))
            return null;

        var limit = int.Parse(limitValues.First());
        var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            ? int.Parse(remainingValues.First())
            : 0;
        var used = response.Headers.TryGetValues("X-RateLimit-Used", out var usedValues)
            ? int.Parse(usedValues.First())
            : limit - remaining;

        DateTimeOffset? resetAt = null;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
            long.TryParse(resetValues.First(), out var resetUnix))
        {
            resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
        }

        return new GitHubRateLimit(limit, remaining, used, resetAt);
    }

    private static GitHubRateLimit ParseRateLimitElement(JsonElement element)
    {
        var limit = element.GetProperty("limit").GetInt32();
        var remaining = element.GetProperty("remaining").GetInt32();
        var used = element.TryGetProperty("used", out var usedProp)
            ? usedProp.GetInt32()
            : limit - remaining;

        DateTimeOffset? resetAt = null;
        if (element.TryGetProperty("resetAt", out var resetProp) &&
            resetProp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetProp.GetString(), out var parsed))
        {
            resetAt = parsed;
        }

        return new GitHubRateLimit(limit, remaining, used, resetAt);
    }
}

using GitDelta.Core.Abstractions;
using System.Text.Json;

namespace GitDelta.GitHub;

public sealed class PullRequestService(
    IGitHubClient gitHubClient,
    ITokenStore tokenStore,
    IAccountService accountService) : IPullRequestService
{
    private const int InboxPageSize = 50;

    private static readonly (InboxSection Section, string QuerySuffix)[] InboxSearches =
    [
        (InboxSection.NeedsMyReview, "is:open is:pr review-requested:@me"),
        (InboxSection.Reviewed, "is:open is:pr reviewed-by:@me"),
        (InboxSection.MyPullRequests, "is:open is:pr author:@me"),
    ];

    public async Task<IReadOnlyList<PullRequestSummary>> GetInboxAsync(CancellationToken ct = default)
    {
        var byNodeId = new Dictionary<string, PullRequestSummary>(StringComparer.Ordinal);

        foreach (var account in accountService.ListAccounts())
        {
            if (account.NeedsReauth)
                continue;

            var token = await tokenStore.GetTokenAsync(account.Host, account.Login, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
                continue;

            foreach (var (section, querySuffix) in InboxSearches)
            {
                IReadOnlyList<PullRequestSummary> summaries;
                try
                {
                    summaries = await SearchInboxSectionAsync(
                            account.Host, account.Login, token, querySuffix, section, ct)
                        .ConfigureAwait(false);
                }
                catch (GitHubApiException ex) when (ex.StatusCode == 401)
                {
                    await accountService.MarkNeedsReauthAsync(account.Host, account.Login, ct)
                        .ConfigureAwait(false);
                    break;
                }

                foreach (var summary in summaries)
                {
                    if (!byNodeId.TryGetValue(summary.NodeId, out var existing) ||
                        SectionPriority(summary.Section) < SectionPriority(existing.Section))
                    {
                        byNodeId[summary.NodeId] = summary;
                    }
                }
            }
        }

        return byNodeId.Values
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();
    }

    public async Task<PullRequestDetail> GetPullRequestAsync(
        string host,
        string accountLogin,
        string owner,
        string name,
        int number,
        CancellationToken ct = default)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);
        var token = await tokenStore.GetTokenAsync(normalizedHost, accountLogin, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No token found for account {accountLogin} on {normalizedHost}.");

        var variables = new { owner, name, number };
        JsonElement data;
        try
        {
            (data, _) = await gitHubClient.ExecuteAsync(
                    normalizedHost,
                    token,
                    EmbeddedQueries.PullRequestDetailQuery,
                    variables,
                    ct)
                .ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == 401)
        {
            await accountService.MarkNeedsReauthAsync(normalizedHost, accountLogin, ct)
                .ConfigureAwait(false);
            throw;
        }

        var repository = data.GetProperty("repository");
        if (repository.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"Repository {owner}/{name} was not found on {normalizedHost}.");
        }

        var pullRequest = repository.GetProperty("pullRequest");
        if (pullRequest.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"Pull request #{number} was not found in {owner}/{name}.");
        }

        return PullRequestGraphQLParser.ParseDetail(
            pullRequest,
            normalizedHost,
            accountLogin,
            InboxSection.MyPullRequests);
    }

    public async Task<int> GetPendingReviewCommentCountAsync(
        string host,
        string accountLogin,
        string owner,
        string name,
        int number,
        CancellationToken ct = default)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);
        var token = await tokenStore.GetTokenAsync(normalizedHost, accountLogin, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No token found for account {accountLogin} on {normalizedHost}.");

        JsonElement data;
        try
        {
            (data, _) = await gitHubClient.ExecuteAsync(
                    normalizedHost,
                    token,
                    EmbeddedQueries.PendingReviewQuery,
                    new { owner, name, number },
                    ct)
                .ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == 401)
        {
            await accountService.MarkNeedsReauthAsync(normalizedHost, accountLogin, ct)
                .ConfigureAwait(false);
            throw;
        }

        return PullRequestGraphQLParser.ParsePendingReviewCommentCount(data, accountLogin);
    }

    private async Task<IReadOnlyList<PullRequestSummary>> SearchInboxSectionAsync(
        string host,
        string accountLogin,
        string token,
        string querySuffix,
        InboxSection section,
        CancellationToken ct)
    {
        var variables = new { query = querySuffix, first = InboxPageSize };
        var (data, _) = await gitHubClient.ExecuteAsync(
                host,
                token,
                EmbeddedQueries.InboxSearchQuery,
                variables,
                ct)
            .ConfigureAwait(false);

        return PullRequestGraphQLParser.ParseInboxSearch(data, host, accountLogin, section);
    }

    private static int SectionPriority(InboxSection section) => section switch
    {
        InboxSection.NeedsMyReview => 0,
        InboxSection.Reviewed => 1,
        InboxSection.MyPullRequests => 2,
        _ => 3,
    };
}

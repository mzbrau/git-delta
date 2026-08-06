using System.Text.Json;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using GitDelta.GitHub;
using GitDelta.Persistence;

namespace GitDelta.Review;

internal sealed class ReviewCommentService(
    IGitHubClient gitHubClient,
    ITokenStore tokenStore,
    ICapabilityCache capabilityCache,
    IReviewOutbox outbox,
    ILocalViewedStore localViewedStore,
    CommentAnchorMapper anchorMapper) : IReviewCommentService
{
    public async Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(
        ReviewSession session,
        CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var token = await GetTokenAsync(summary.Host, summary.AccountLogin, ct).ConfigureAwait(false);
        var variables = new
        {
            owner = summary.Owner,
            name = summary.Name,
            number = summary.Number,
        };

        var (data, _) = await gitHubClient.ExecuteAsync(
                GitHubClient.NormalizeHost(summary.Host),
                token,
                EmbeddedQueries.PullRequestThreadsQuery,
                variables,
                ct)
            .ConfigureAwait(false);

        return ReviewThreadParser.Parse(data);
    }

    public Task<IReadOnlyList<ReviewThread>> ResolveAnchorsAsync(
        ReviewSession session,
        IReadOnlyList<ReviewThread> threads,
        FilePath path,
        FileDiff fileDiff,
        CancellationToken ct = default) =>
        anchorMapper.MapThreadsAsync(session, threads, path, fileDiff, ct);

    public async Task AddPendingCommentAsync(
        ReviewSession session,
        string body,
        FilePath path,
        int? line,
        int? startLine,
        string side,
        CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var payload = Wrap(summary, new AddCommentPayload(
            Guid.NewGuid().ToString("N"),
            path.Value,
            line,
            startLine,
            side,
            body,
            session.Head.Value));

        await outbox.EnqueueAsync(CreateEntry(summary, OutboxKind.AddComment, payload), ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MentionableUser>> GetMentionableUsersAsync(
        ReviewSession session,
        string? query,
        CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var token = await GetTokenAsync(summary.Host, summary.AccountLogin, ct).ConfigureAwait(false);
        var variables = new
        {
            owner = summary.Owner,
            name = summary.Name,
            query = string.IsNullOrWhiteSpace(query) ? null : query,
            first = 20,
        };

        var (data, _) = await gitHubClient.ExecuteAsync(
                GitHubClient.NormalizeHost(summary.Host),
                token,
                EmbeddedQueries.MentionableUsersQuery,
                variables,
                ct)
            .ConfigureAwait(false);

        return MentionableUsersParser.Parse(data);
    }

    public async Task ReplyCommentAsync(
        ReviewSession session,
        string threadId,
        string body,
        CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var payload = Wrap(summary, new ReplyCommentPayload(Guid.NewGuid().ToString("N"), threadId, body));
        await outbox.EnqueueAsync(CreateEntry(summary, OutboxKind.ReplyComment, payload), ct)
            .ConfigureAwait(false);
    }

    public async Task EditCommentAsync(
        ReviewSession session,
        string commentId,
        string body,
        CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var payload = Wrap(summary, new EditCommentPayload(commentId, body));
        await outbox.EnqueueAsync(CreateEntry(summary, OutboxKind.EditComment, payload), ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteCommentAsync(
        ReviewSession session,
        string commentId,
        CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var payload = Wrap(summary, new DeleteCommentPayload(commentId));
        await outbox.EnqueueAsync(CreateEntry(summary, OutboxKind.DeleteComment, payload), ct)
            .ConfigureAwait(false);
    }

    public async Task ResolveThreadAsync(ReviewSession session, string threadId, CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var payload = Wrap(summary, new ResolveThreadPayload(threadId));
        await outbox.EnqueueAsync(CreateEntry(summary, OutboxKind.ResolveThread, payload), ct)
            .ConfigureAwait(false);
    }

    public async Task UnresolveThreadAsync(ReviewSession session, string threadId, CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var payload = Wrap(summary, new UnresolveThreadPayload(threadId));
        await outbox.EnqueueAsync(CreateEntry(summary, OutboxKind.UnresolveThread, payload), ct)
            .ConfigureAwait(false);
    }

    public async Task MarkFileViewedAsync(ReviewSession session, FilePath path, CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var host = GitHubClient.NormalizeHost(summary.Host);
        var capabilities = await GetCapabilitiesAsync(host, summary.AccountLogin, ct).ConfigureAwait(false);

        // Always cache locally so UI / filters survive remote-only outbox writes.
        await localViewedStore.SetViewedAsync(
                summary.NodeId,
                path.Value,
                session.Head.Value,
                DateTimeOffset.UtcNow,
                ct)
            .ConfigureAwait(false);

        if (capabilities.MarkFileAsViewed)
        {
            var payload = Wrap(summary, new MarkFileViewedPayload(path.Value));
            await outbox.EnqueueAsync(CreateEntry(summary, OutboxKind.MarkFileViewed, payload), ct)
                .ConfigureAwait(false);
        }
    }

    public async Task UnmarkFileViewedAsync(ReviewSession session, FilePath path, CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var host = GitHubClient.NormalizeHost(summary.Host);
        var capabilities = await GetCapabilitiesAsync(host, summary.AccountLogin, ct).ConfigureAwait(false);

        await localViewedStore.RemoveViewedAsync(summary.NodeId, path.Value, ct).ConfigureAwait(false);

        if (capabilities.MarkFileAsViewed)
        {
            var payload = Wrap(summary, new UnmarkFileViewedPayload(path.Value));
            await outbox.EnqueueAsync(CreateEntry(summary, OutboxKind.UnmarkFileViewed, payload), ct)
                .ConfigureAwait(false);
        }
    }

    public async Task SubmitReviewAsync(
        ReviewSession session,
        SubmitReviewEvent reviewEvent,
        string? body,
        CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var payload = Wrap(summary, new SubmitReviewPayload(reviewEvent.ToString(), body, session.Head.Value));
        var entry = CreateEntry(summary, OutboxKind.SubmitReview, payload);
        await outbox.EnqueueAsync(entry, ct).ConfigureAwait(false);
        await outbox.DrainSubmitAsync(entry.Id, ct).ConfigureAwait(false);
    }

    public async Task<bool> SupportsRemoteViewedStateAsync(ReviewSession session, CancellationToken ct = default)
    {
        var summary = session.Detail.Summary;
        var host = GitHubClient.NormalizeHost(summary.Host);
        var capabilities = await GetCapabilitiesAsync(host, summary.AccountLogin, ct).ConfigureAwait(false);
        return capabilities.MarkFileAsViewed;
    }

    private async Task<string> GetTokenAsync(string host, string login, CancellationToken ct)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);
        return await tokenStore.GetTokenAsync(normalizedHost, login, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No token for {login} on {normalizedHost}.");
    }

    private async Task<GitHubCapabilities> GetCapabilitiesAsync(string host, string login, CancellationToken ct)
    {
        var key = new CapabilityCacheKey(host, login);
        if (capabilityCache.TryGet(key, out var cached))
            return cached;

        var token = await GetTokenAsync(host, login, ct).ConfigureAwait(false);
        var capabilities = await gitHubClient.ProbeCapabilitiesAsync(host, token, ct).ConfigureAwait(false);
        capabilityCache.Set(key, capabilities);
        return capabilities;
    }

    private static string Wrap<T>(PullRequestSummary summary, T data) =>
        JsonSerializer.Serialize(
            new OutboxPayloadEnvelope<T>(summary.Owner, summary.Name, summary.Number, data),
            ReviewJson.Options);

    private static OutboxEntry CreateEntry(PullRequestSummary summary, OutboxKind kind, string payloadJson) =>
        new(
            Guid.NewGuid().ToString("N"),
            GitHubClient.NormalizeHost(summary.Host),
            summary.AccountLogin,
            summary.NodeId,
            kind,
            payloadJson,
            DateTimeOffset.UtcNow,
            Attempts: 0,
            LastError: null,
            OutboxState.Pending);
}

using System.Text.Json;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.GitHub;
using CodeReviewr.Persistence;

namespace CodeReviewr.Review;

internal sealed record OutboxPayloadEnvelope<T>(string Owner, string Name, int Number, T Data);

internal sealed class ReviewMutationExecutor(
    IGitHubClient gitHubClient,
    ITokenStore tokenStore)
{
    public async Task ExecuteOutboxEntryAsync(OutboxEntry entry, CancellationToken ct)
    {
        var token = await GetTokenAsync(entry.AccountHost, entry.AccountLogin, ct).ConfigureAwait(false);
        var host = GitHubClient.NormalizeHost(entry.AccountHost);

        switch (entry.Kind)
        {
            case OutboxKind.AddComment:
                await ExecuteAddCommentAsync(host, token, entry, ct).ConfigureAwait(false);
                break;
            case OutboxKind.ReplyComment:
                await ExecuteReplyCommentAsync(host, token, entry, ct).ConfigureAwait(false);
                break;
            case OutboxKind.EditComment:
                await ExecuteEditCommentAsync(host, token, entry, ct).ConfigureAwait(false);
                break;
            case OutboxKind.DeleteComment:
                await ExecuteDeleteCommentAsync(host, token, entry, ct).ConfigureAwait(false);
                break;
            case OutboxKind.ResolveThread:
                await ExecuteResolveThreadAsync(host, token, entry, resolve: true, ct).ConfigureAwait(false);
                break;
            case OutboxKind.UnresolveThread:
                await ExecuteResolveThreadAsync(host, token, entry, resolve: false, ct).ConfigureAwait(false);
                break;
            case OutboxKind.MarkFileViewed:
                await ExecuteMarkFileViewedAsync(host, token, entry, viewed: true, ct).ConfigureAwait(false);
                break;
            case OutboxKind.UnmarkFileViewed:
                await ExecuteMarkFileViewedAsync(host, token, entry, viewed: false, ct).ConfigureAwait(false);
                break;
            case OutboxKind.SubmitReview:
                await ExecuteSubmitReviewAsync(host, token, entry, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported outbox kind {entry.Kind}.");
        }
    }

    private async Task ExecuteAddCommentAsync(
        string host,
        string token,
        OutboxEntry entry,
        CancellationToken ct)
    {
        var envelope = DeserializeEnvelope<AddCommentPayload>(entry.PayloadJson);
        var (owner, name, number) = (envelope.Owner, envelope.Name, envelope.Number);
        var payload = envelope.Data;
        await EnsurePendingReviewIdAsync(host, token, owner, name, number, ct).ConfigureAwait(false);

        var input = new Dictionary<string, object?>
        {
            ["pullRequestId"] = entry.PrNodeId,
            ["body"] = payload.Body,
            ["path"] = payload.Path,
            ["side"] = payload.Side,
        };
        if (payload.Line is not null)
            input["line"] = payload.Line;
        if (payload.StartLine is not null)
            input["startLine"] = payload.StartLine;

        await gitHubClient.ExecuteAsync(
                host,
                token,
                EmbeddedQueries.AddPullRequestReviewThreadMutation,
                new { input },
                ct)
            .ConfigureAwait(false);
    }

    private async Task ExecuteReplyCommentAsync(
        string host,
        string token,
        OutboxEntry entry,
        CancellationToken ct)
    {
        var payload = DeserializeEnvelope<ReplyCommentPayload>(entry.PayloadJson).Data;
        await gitHubClient.ExecuteAsync(
                host,
                token,
                EmbeddedQueries.AddPullRequestReviewCommentMutation,
                new { input = new { pullRequestReviewThreadId = payload.ThreadId, body = payload.Body } },
                ct)
            .ConfigureAwait(false);
    }

    private async Task ExecuteEditCommentAsync(
        string host,
        string token,
        OutboxEntry entry,
        CancellationToken ct)
    {
        var payload = DeserializeEnvelope<EditCommentPayload>(entry.PayloadJson).Data;
        await gitHubClient.ExecuteAsync(
                host,
                token,
                EmbeddedQueries.UpdatePullRequestReviewCommentMutation,
                new { input = new { pullRequestReviewCommentId = payload.CommentId, body = payload.Body } },
                ct)
            .ConfigureAwait(false);
    }

    private async Task ExecuteDeleteCommentAsync(
        string host,
        string token,
        OutboxEntry entry,
        CancellationToken ct)
    {
        var payload = DeserializeEnvelope<DeleteCommentPayload>(entry.PayloadJson).Data;
        await gitHubClient.ExecuteAsync(
                host,
                token,
                EmbeddedQueries.DeletePullRequestReviewCommentMutation,
                new { input = new { id = payload.CommentId } },
                ct)
            .ConfigureAwait(false);
    }

    private async Task ExecuteResolveThreadAsync(
        string host,
        string token,
        OutboxEntry entry,
        bool resolve,
        CancellationToken ct)
    {
        var threadId = resolve
            ? DeserializeEnvelope<ResolveThreadPayload>(entry.PayloadJson).Data.ThreadId
            : DeserializeEnvelope<UnresolveThreadPayload>(entry.PayloadJson).Data.ThreadId;

        var mutation = resolve
            ? EmbeddedQueries.ResolveReviewThreadMutation
            : EmbeddedQueries.UnresolveReviewThreadMutation;

        await gitHubClient.ExecuteAsync(
                host,
                token,
                mutation,
                new { input = new { threadId } },
                ct)
            .ConfigureAwait(false);
    }

    private async Task ExecuteMarkFileViewedAsync(
        string host,
        string token,
        OutboxEntry entry,
        bool viewed,
        CancellationToken ct)
    {
        if (viewed)
        {
            var payload = DeserializeEnvelope<MarkFileViewedPayload>(entry.PayloadJson).Data;
            await gitHubClient.ExecuteAsync(
                    host,
                    token,
                    EmbeddedQueries.MarkFileAsViewedMutation,
                    new { input = new { pullRequestId = entry.PrNodeId, path = payload.Path, commitOid = payload.CommitOid } },
                    ct)
                .ConfigureAwait(false);
        }
        else
        {
            var payload = DeserializeEnvelope<UnmarkFileViewedPayload>(entry.PayloadJson).Data;
            await gitHubClient.ExecuteAsync(
                    host,
                    token,
                    EmbeddedQueries.UnmarkFileAsViewedMutation,
                    new { input = new { pullRequestId = entry.PrNodeId, path = payload.Path, commitOid = payload.CommitOid } },
                    ct)
                .ConfigureAwait(false);
        }
    }

    internal async Task ExecuteSubmitReviewAsync(
        string host,
        string token,
        OutboxEntry entry,
        CancellationToken ct)
    {
        var envelope = DeserializeEnvelope<SubmitReviewPayload>(entry.PayloadJson);
        var payload = envelope.Data;
        var variables = new { owner = envelope.Owner, name = envelope.Name, number = envelope.Number };
        var (data, _) = await gitHubClient.ExecuteAsync(
                host,
                token,
                EmbeddedQueries.PendingReviewQuery,
                variables,
                ct)
            .ConfigureAwait(false);

        var pullRequest = data.GetProperty("repository").GetProperty("pullRequest");
        var actualHead = pullRequest.GetProperty("headRefOid").GetString()
            ?? throw new InvalidOperationException("Missing headRefOid.");
        if (!string.Equals(actualHead, payload.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase))
            throw new HeadMovedException(payload.ExpectedHeadSha, actualHead);

        var reviewId = await EnsurePendingReviewIdAsync(
                host,
                token,
                envelope.Owner,
                envelope.Name,
                envelope.Number,
                ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not create pending review.");

        var githubEvent = payload.Event switch
        {
            nameof(SubmitReviewEvent.Approve) => "APPROVE",
            nameof(SubmitReviewEvent.Comment) => "COMMENT",
            nameof(SubmitReviewEvent.RequestChanges) => "REQUEST_CHANGES",
            _ => throw new InvalidOperationException($"Unknown submit event {payload.Event}."),
        };

        var input = new Dictionary<string, object?>
        {
            ["pullRequestReviewId"] = reviewId,
            ["event"] = githubEvent,
        };
        if (!string.IsNullOrWhiteSpace(payload.Body))
            input["body"] = payload.Body;

        await gitHubClient.ExecuteAsync(
                host,
                token,
                EmbeddedQueries.SubmitPullRequestReviewMutation,
                new { input },
                ct)
            .ConfigureAwait(false);
    }

    private async Task<string?> EnsurePendingReviewIdAsync(
        string host,
        string token,
        string owner,
        string name,
        int number,
        CancellationToken ct)
    {
        var variables = new { owner, name, number };
        var (data, _) = await gitHubClient.ExecuteAsync(
                host,
                token,
                EmbeddedQueries.PendingReviewQuery,
                variables,
                ct)
            .ConfigureAwait(false);

        var pullRequest = data.GetProperty("repository").GetProperty("pullRequest");
        foreach (var review in pullRequest.GetProperty("reviews").GetProperty("nodes").EnumerateArray())
        {
            if (review.GetProperty("state").GetString() == "PENDING")
                return review.GetProperty("id").GetString();
        }

        var prId = pullRequest.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Pull request id missing from GraphQL response.");
        var (createData, _) = await gitHubClient.ExecuteAsync(
                host,
                token,
                EmbeddedQueries.AddPullRequestReviewMutation,
                new { input = new { pullRequestId = prId, @event = "PENDING" } },
                ct)
            .ConfigureAwait(false);

        return createData.GetProperty("addPullRequestReview")
            .GetProperty("pullRequestReview")
            .GetProperty("id")
            .GetString();
    }

    private async Task<string> GetTokenAsync(string host, string login, CancellationToken ct)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);
        return await tokenStore.GetTokenAsync(normalizedHost, login, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No token for {login} on {normalizedHost}.");
    }

    private static OutboxPayloadEnvelope<T> DeserializeEnvelope<T>(string json) =>
        JsonSerializer.Deserialize<OutboxPayloadEnvelope<T>>(json, ReviewJson.Options)
        ?? throw new InvalidOperationException("Invalid outbox payload.");
}

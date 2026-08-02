using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diff;
using CodeReviewr.GitHub;
using CodeReviewr.Persistence;

namespace CodeReviewr.Review;

public interface IReviewService
{
    Task<ReviewSession> OpenAsync(PullRequestSummary summary, CancellationToken ct = default);

    Task<FileDiff> GetDiffAsync(
        ReviewSession session,
        FilePath path,
        DiffOptions options,
        CancellationToken ct = default);
}

public interface IReviewCommentService
{
    Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(ReviewSession session, CancellationToken ct = default);

    Task<IReadOnlyList<ReviewThread>> ResolveAnchorsAsync(
        ReviewSession session,
        IReadOnlyList<ReviewThread> threads,
        FilePath path,
        FileDiff fileDiff,
        CancellationToken ct = default);

    Task AddPendingCommentAsync(
        ReviewSession session,
        string body,
        FilePath path,
        int? line,
        int? startLine,
        string side,
        CancellationToken ct = default);

    Task ReplyCommentAsync(
        ReviewSession session,
        string threadId,
        string body,
        CancellationToken ct = default);

    Task EditCommentAsync(ReviewSession session, string commentId, string body, CancellationToken ct = default);

    Task DeleteCommentAsync(ReviewSession session, string commentId, CancellationToken ct = default);

    Task ResolveThreadAsync(ReviewSession session, string threadId, CancellationToken ct = default);

    Task UnresolveThreadAsync(ReviewSession session, string threadId, CancellationToken ct = default);

    Task MarkFileViewedAsync(ReviewSession session, FilePath path, CancellationToken ct = default);

    Task UnmarkFileViewedAsync(ReviewSession session, FilePath path, CancellationToken ct = default);

    Task SubmitReviewAsync(
        ReviewSession session,
        SubmitReviewEvent reviewEvent,
        string? body,
        CancellationToken ct = default);

    Task<bool> SupportsRemoteViewedStateAsync(ReviewSession session, CancellationToken ct = default);
}

public interface IReviewOutbox
{
    bool IsOffline { get; }

    event EventHandler? DrainCompleted;

    Task EnqueueAsync(OutboxEntry entry, CancellationToken ct = default);

    Task DrainAsync(CancellationToken ct = default);

    Task DrainSubmitAsync(string entryId, CancellationToken ct = default);

    Task<IReadOnlyList<OutboxEntry>> ListPendingAsync(string? prNodeId = null, CancellationToken ct = default);
}

public interface IReviewSessionStore;

namespace CodeReviewr.Persistence;

public enum OutboxState
{
    Pending,
    InFlight,
    Failed,
}

public enum OutboxKind
{
    AddComment,
    ReplyComment,
    EditComment,
    DeleteComment,
    ResolveThread,
    UnresolveThread,
    MarkFileViewed,
    UnmarkFileViewed,
    SubmitReview,
}

public sealed record OutboxEntry(
    string Id,
    string AccountHost,
    string AccountLogin,
    string PrNodeId,
    OutboxKind Kind,
    string PayloadJson,
    DateTimeOffset CreatedUtc,
    int Attempts,
    string? LastError,
    OutboxState State);

public sealed record LocalViewedEntry(
    string PrNodeId,
    string Path,
    string ContentId,
    DateTimeOffset ViewedUtc);

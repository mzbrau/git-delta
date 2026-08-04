using CodeReviewr.Core.AI;

namespace CodeReviewr.Core.Abstractions;

/// <summary>Durable AI result store (lives in durable.db schema v4).</summary>
public interface IAiResultStore
{
    Task UpsertRunAsync(AiRunRecord run, CancellationToken ct = default);
    Task<AiRunRecord?> GetLatestRunAsync(string sessionKey, CancellationToken ct = default);
    Task<AiRunRecord?> GetRunAsync(string runId, CancellationToken ct = default);

    Task UpsertPrResultAsync(AiPrResultRecord result, CancellationToken ct = default);
    Task<AiPrResultRecord?> GetPrResultByCacheKeyAsync(string cacheKey, CancellationToken ct = default);
    Task<AiPrResultRecord?> GetPrResultForRunAsync(string runId, CancellationToken ct = default);

    Task UpsertFileResultAsync(AiFileResultRecord result, CancellationToken ct = default);
    Task<AiFileResultRecord?> GetFileResultByCacheKeyAsync(string cacheKey, CancellationToken ct = default);
    Task<IReadOnlyList<AiFileResultRecord>> ListFileResultsForRunAsync(string runId, CancellationToken ct = default);

    Task UpsertAnnotationAsync(AiAnnotationRecord annotation, CancellationToken ct = default);
    Task<IReadOnlyList<AiAnnotationRecord>> ListAnnotationsAsync(
        string sessionKey,
        string? path = null,
        bool includeDismissed = false,
        CancellationToken ct = default);
    Task SetAnnotationReadStateAsync(string id, AiAnnotationReadState state, CancellationToken ct = default);

    Task AppendChatMessageAsync(string sessionKey, AiChatMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<AiChatMessage>> ListChatMessagesAsync(string sessionKey, CancellationToken ct = default);
    Task ClearChatMessagesAsync(string sessionKey, CancellationToken ct = default);

    Task ClearAllAsync(CancellationToken ct = default);
}

public sealed record AiRunRecord(
    string Id,
    string SessionKey,
    string HeadSha,
    string MergeBaseSha,
    string? CopilotSessionId,
    AiRunState State,
    int TurnsUsed,
    string? AdHocInstructions,
    string CacheKey,
    string? ErrorMessage,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc);

public sealed record AiPrResultRecord(
    string RunId,
    string SessionKey,
    string CacheKey,
    string PayloadJson,
    DateTimeOffset UpdatedUtc);

public sealed record AiFileResultRecord(
    string RunId,
    string SessionKey,
    string Path,
    string CacheKey,
    string? Classification,
    int PriorityStars,
    string? Guidance,
    string? SummaryJson,
    DateTimeOffset UpdatedUtc);

public sealed record AiAnnotationRecord(
    string Id,
    string RunId,
    string SessionKey,
    string Path,
    string BlobOid,
    int StartLine,
    int EndLine,
    string Side,
    string Severity,
    string Body,
    AiAnnotationReadState ReadState,
    DateTimeOffset UpdatedUtc);

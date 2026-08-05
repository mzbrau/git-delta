using CodeReviewr.Core.AI;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.Core.Abstractions;

/// <summary>
/// Phase 3 AI review surface. Implementations must read only via revision-pinned trees /
/// materialised exports, emit overlays (never mutate <see cref="FileDiff"/> / <see cref="IDiffCache"/>),
/// and honour privacy settings / cancellation budgets.
/// </summary>
public interface IAIReviewService
{
    ValueTask<IReadOnlyList<IDiffAnnotation>> GetAnnotationsAsync(
        FileDiffKey key,
        CancellationToken ct = default);

    ValueTask<AiRunSnapshot?> GetCachedRunAsync(string sessionKey, CancellationToken ct = default);

    /// <summary>
    /// Hydrates in-memory run context from a durable cached run (no live agent session yet).
    /// Call after opening a PR that already has a completed AI review so chat/ask can lazily resume.
    /// </summary>
    Task AttachCachedRunAsync(AiReviewRequest request, CancellationToken ct = default);

    Task<AiRunSnapshot> StartReviewAsync(AiReviewRequest request, CancellationToken ct = default);

    Task CancelAsync(string repositoryKey, CancellationToken ct = default);

    IDisposable ObserveProgress(string repositoryKey, Action<AiRunProgress> handler);

    /// <summary>Append-only activity log lines (prompt, assistant text, tools, wait status).</summary>
    IDisposable ObserveActivityLog(string repositoryKey, Action<string> handler);

    Task<AiConnectionProbeResult> TestConnectionAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);

    Task RequestFileDepthAsync(AiFileDepthRequest request, CancellationToken ct = default);

    ValueTask<AiFileBriefingResult?> GetFileBriefingAsync(
        string sessionKey,
        string path,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<AiAnnotationResult>> GetFileAnnotationsAsync(
        string sessionKey,
        string path,
        bool includeDismissed = false,
        CancellationToken ct = default);

    Task SetAnnotationReadStateAsync(
        string annotationId,
        AiAnnotationReadState state,
        CancellationToken ct = default);

    Task<string> AskAsync(AiQuestionRequest request, CancellationToken ct = default);

    Task<string> RunInlineActionAsync(AiInlineActionRequest request, CancellationToken ct = default);

    Task<string> ChatAsync(AiQuestionRequest request, CancellationToken ct = default);

    ValueTask<IReadOnlyList<AiChatMessage>> GetChatHistoryAsync(
        string sessionKey,
        CancellationToken ct = default);

    Task ClearChatHistoryAsync(string sessionKey, CancellationToken ct = default);

    Task ClearAiDataAsync(CancellationToken ct = default);
}

/// <summary>No-op AI service used when AI is disabled or unavailable.</summary>
public sealed class NullAIReviewService : IAIReviewService
{
    public static NullAIReviewService Instance { get; } = new();

    public ValueTask<IReadOnlyList<IDiffAnnotation>> GetAnnotationsAsync(
        FileDiffKey key,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<IDiffAnnotation>>([]);

    public ValueTask<AiRunSnapshot?> GetCachedRunAsync(string sessionKey, CancellationToken ct = default) =>
        ValueTask.FromResult<AiRunSnapshot?>(null);

    public Task AttachCachedRunAsync(AiReviewRequest request, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<AiRunSnapshot> StartReviewAsync(AiReviewRequest request, CancellationToken ct = default) =>
        Task.FromResult(new AiRunSnapshot(
            RunId: Guid.NewGuid().ToString("N"),
            SessionKey: request.SessionKey,
            HeadSha: request.HeadSha,
            MergeBaseSha: request.MergeBaseSha,
            State: AiRunState.Failed,
            CopilotSessionId: null,
            TurnsUsed: 0,
            AdHocInstructions: request.AdHocInstructions,
            ChangeBriefing: null,
            ErrorMessage: "AI assistance is not available.",
            StartedUtc: DateTimeOffset.UtcNow,
            FinishedUtc: DateTimeOffset.UtcNow));

    public Task CancelAsync(string repositoryKey, CancellationToken ct = default) => Task.CompletedTask;

    public IDisposable ObserveProgress(string repositoryKey, Action<AiRunProgress> handler) =>
        NullDisposable.Instance;

    public IDisposable ObserveActivityLog(string repositoryKey, Action<string> handler) =>
        NullDisposable.Instance;

    public Task<AiConnectionProbeResult> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(new AiConnectionProbeResult(false, "AI assistance is not available."));

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task RequestFileDepthAsync(AiFileDepthRequest request, CancellationToken ct = default) =>
        Task.CompletedTask;

    public ValueTask<AiFileBriefingResult?> GetFileBriefingAsync(
        string sessionKey,
        string path,
        CancellationToken ct = default) =>
        ValueTask.FromResult<AiFileBriefingResult?>(null);

    public ValueTask<IReadOnlyList<AiAnnotationResult>> GetFileAnnotationsAsync(
        string sessionKey,
        string path,
        bool includeDismissed = false,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<AiAnnotationResult>>([]);

    public Task SetAnnotationReadStateAsync(
        string annotationId,
        AiAnnotationReadState state,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<string> AskAsync(AiQuestionRequest request, CancellationToken ct = default) =>
        Task.FromResult("AI assistance is not available.");

    public Task<string> RunInlineActionAsync(AiInlineActionRequest request, CancellationToken ct = default) =>
        Task.FromResult("AI assistance is not available.");

    public Task<string> ChatAsync(AiQuestionRequest request, CancellationToken ct = default) =>
        Task.FromResult("AI assistance is not available.");

    public ValueTask<IReadOnlyList<AiChatMessage>> GetChatHistoryAsync(
        string sessionKey,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<AiChatMessage>>([]);

    public Task ClearChatHistoryAsync(string sessionKey, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ClearAiDataAsync(CancellationToken ct = default) => Task.CompletedTask;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

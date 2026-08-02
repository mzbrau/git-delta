using CodeReviewr.Core.Diff;

namespace CodeReviewr.Core.Abstractions;

/// <summary>
/// Phase 3 AI review surface. Implementations must read only via <see cref="IReviewTree"/>-equivalent
/// revision-pinned views, emit overlays (never mutate <see cref="FileDiff"/> / <see cref="IDiffCache"/>),
/// and honour privacy settings / cancellation budgets.
/// </summary>
public interface IAIReviewService
{
    /// <summary>Suggested review file order for a pull request / review session key.</summary>
    ValueTask<IReadOnlyList<FilePath>> SuggestFileOrderAsync(
        string sessionKey,
        IReadOnlyList<FilePath> changedFiles,
        CancellationToken ct = default);

    /// <summary>Short risk / summary checklist items for the review session.</summary>
    ValueTask<IReadOnlyList<AIChecklistItem>> GetChecklistAsync(
        string sessionKey,
        CancellationToken ct = default);

    /// <summary>Optional line-level annotations for a content-addressed diff key.</summary>
    ValueTask<IReadOnlyList<IDiffAnnotation>> GetAnnotationsAsync(
        FileDiffKey key,
        CancellationToken ct = default);
}

/// <summary>One AI-generated checklist / risk item shown beside a review.</summary>
public sealed record AIChecklistItem(
    string Id,
    string Title,
    string? Detail,
    AIChecklistSeverity Severity);

public enum AIChecklistSeverity
{
    Info,
    Suggestion,
    Warning,
    Risk,
}

/// <summary>No-op AI service used until Phase 3 Copilot (or other) SDK is wired.</summary>
public sealed class NullAIReviewService : IAIReviewService
{
    public static NullAIReviewService Instance { get; } = new();

    public ValueTask<IReadOnlyList<FilePath>> SuggestFileOrderAsync(
        string sessionKey,
        IReadOnlyList<FilePath> changedFiles,
        CancellationToken ct = default) =>
        ValueTask.FromResult(changedFiles);

    public ValueTask<IReadOnlyList<AIChecklistItem>> GetChecklistAsync(
        string sessionKey,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<AIChecklistItem>>([]);

    public ValueTask<IReadOnlyList<IDiffAnnotation>> GetAnnotationsAsync(
        FileDiffKey key,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<IDiffAnnotation>>([]);
}

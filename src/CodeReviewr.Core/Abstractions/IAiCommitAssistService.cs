using CodeReviewr.Core.AI;

namespace CodeReviewr.Core.Abstractions;

/// <summary>
/// Copilot-backed helpers for commit message generation and Magic Commit planning.
/// Implementations must not mutate the working copy — execution belongs to the host.
/// </summary>
public interface IAiCommitAssistService
{
    /// <summary>Generates a commit message for the provided staged-diff summary.</summary>
    Task<string> GenerateCommitMessageAsync(
        string repositoryKey,
        string repositoryPath,
        string diffSummary,
        CancellationToken ct = default);

    /// <summary>
    /// Asks the agent to group the given hunk inventory into logical commits.
    /// Returns a plan only — the host stages/commits.
    /// </summary>
    /// <param name="activity">Optional append-only activity log (prompt, assistant text, tools).</param>
    Task<MagicCommitPlan> ProposeMagicCommitPlanAsync(
        string repositoryKey,
        string repositoryPath,
        string hunkInventory,
        string? adHocInstructions,
        IProgress<string>? activity = null,
        CancellationToken ct = default);
}

/// <summary>No-op commit assist used when AI is unavailable.</summary>
public sealed class NullAiCommitAssistService : IAiCommitAssistService
{
    public static NullAiCommitAssistService Instance { get; } = new();

    public Task<string> GenerateCommitMessageAsync(
        string repositoryKey,
        string repositoryPath,
        string diffSummary,
        CancellationToken ct = default) =>
        Task.FromException<string>(new InvalidOperationException("AI commit assistance is not available."));

    public Task<MagicCommitPlan> ProposeMagicCommitPlanAsync(
        string repositoryKey,
        string repositoryPath,
        string hunkInventory,
        string? adHocInstructions,
        IProgress<string>? activity = null,
        CancellationToken ct = default) =>
        Task.FromException<MagicCommitPlan>(new InvalidOperationException("AI commit assistance is not available."));
}

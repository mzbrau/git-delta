using GitDelta.Core;
using GitDelta.Core.AI;

namespace GitDelta.App.ViewModels;

/// <summary>
/// Shared chrome formatting for AI review surfaces (PR review and pending-changes).
/// Host ViewModels own mutable run state; this type keeps labels/diagnostics DRY.
/// </summary>
public static class AiReviewSessionViewModel
{
    public static string FormatProgressText(AiRunProgress? progress)
    {
        if (progress is null) return "";

        var stage = progress.Stage.ToString();
        var elapsed = progress.Elapsed < TimeSpan.FromHours(1)
            ? progress.Elapsed.ToString(@"mm\:ss")
            : progress.Elapsed.ToString(@"h\:mm\:ss");
        var turns = progress.TurnBudget is > 0
            ? $"{progress.TurnsUsed}/{progress.TurnBudget} turns"
            : $"{progress.TurnsUsed} turns";
        var files = progress.FilesTotal > 0
            ? $" · {progress.FilesCompleted}/{progress.FilesTotal} files"
            : "";
        var message = string.IsNullOrWhiteSpace(progress.Message) ? "" : $" — {progress.Message}";
        return $"{stage} · {elapsed} · {turns}{files}{message}";
    }

    public static string StatusDialogTitle(AiRunState state) => state switch
    {
        AiRunState.Running => "AI review in progress",
        AiRunState.Complete => "AI review complete",
        AiRunState.Failed => "AI review failed",
        AiRunState.Incomplete => "AI review incomplete",
        AiRunState.PausedBudget => "AI review paused",
        _ => "AI review status",
    };

    public static string ButtonLabel(AiRunState state) => state switch
    {
        AiRunState.Running => "Reviewing…",
        AiRunState.Incomplete or AiRunState.PausedBudget => "Resume AI review",
        AiRunState.Complete => "Re-run AI review",
        AiRunState.Failed => "Retry AI review",
        _ => "AI review",
    };

    public static bool HasDiagnostics(
        AiRunState state,
        string? lastError,
        string? activityLog) =>
        !string.IsNullOrWhiteSpace(lastError) ||
        !string.IsNullOrWhiteSpace(activityLog) ||
        state is AiRunState.Failed or AiRunState.Incomplete or AiRunState.Running;

    public static string FormatDiagnosticsText(
        AppSettings settings,
        AiRunState state,
        AiRunProgress? progress,
        string? copilotSessionId,
        string? lastError,
        string activityLog)
    {
        var runTimeout = settings.AiRunTimeoutSeconds <= 0
            ? "unlimited"
            : $"{settings.AiRunTimeoutSeconds}s";
        var lines = new List<string>
        {
            $"State: {state}",
            $"Turn idle timeout: {settings.AiTurnTimeoutSeconds}s",
            $"Run timeout: {runTimeout}",
            $"Turn budget: {settings.AiTurnBudget}",
        };

        if (progress is { } p)
        {
            lines.Add($"Stage: {p.Stage}");
            lines.Add($"Elapsed: {p.Elapsed:g}");
            lines.Add($"Turns used: {p.TurnsUsed}");
            if (!string.IsNullOrWhiteSpace(p.Message))
                lines.Add($"Progress: {p.Message}");
        }

        if (!string.IsNullOrWhiteSpace(copilotSessionId))
            lines.Add($"Copilot session: {copilotSessionId}");

        if (!string.IsNullOrWhiteSpace(lastError))
            lines.Add($"Error: {lastError}");

        if (!string.IsNullOrWhiteSpace(activityLog))
        {
            lines.Add("");
            lines.Add("--- Activity log ---");
            lines.Add(activityLog.TrimEnd());
        }

        return string.Join(Environment.NewLine, lines);
    }
}

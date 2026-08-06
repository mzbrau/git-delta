using GitDelta.AI.Agent;
using GitDelta.Core.AI;

namespace GitDelta.AI;

/// <summary>
/// Mutable per-session run state for an in-flight or attached AI review.
/// Extracted from <see cref="AiReviewCoordinator"/> so lifecycle, chat, and query helpers
/// can share one explicit type instead of a nested blob.
/// </summary>
internal sealed class AiActiveRunState
{
    public AiReviewRequest Request = null!;
    public string SessionKey = "";
    public string RepositoryKey = "";
    public string RepositoryPath = "";
    public string HeadSha = "";
    public string MergeBaseSha = "";
    public string? RunId;
    public string? CacheKey;
    public string? CopilotSessionId;
    public IAgentSession? Session;
    public readonly SemaphoreSlim SessionGate = new(1, 1);
    public string? MaterialisedPath;
    public string? Model;
    public string? RulesHash;
    public string? InstructionsHash;
    public string? AdHocInstructions;
    public int TurnsUsed;
    public int RunTimeoutSeconds;
    public int FilesCompleted;
    public int FilesTotal;
    public bool UserCancelled;
    public bool TurnIdleTimedOut;
    public bool AwaitingChangeBriefing;
    public AiRunState State = AiRunState.Idle;
    public AiChangeBriefingResult? ChangeBriefing;
    public string? ErrorMessage;
    public DateTimeOffset StartedUtc;
    public DateTimeOffset? FinishedUtc;
    public CancellationTokenSource? RunCts;
    public FileDepthContext? CurrentFileRequest;
    public Action? ReportAgentActivity;
}

/// <summary>Context for the file currently under depth review (used by annotation OID resolution).</summary>
internal sealed record FileDepthContext(
    string Path,
    string? BeforeOid,
    string? AfterOid,
    int? ChangePercent,
    int? LinesAdded,
    int? LinesRemoved);

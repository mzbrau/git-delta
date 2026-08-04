using CodeReviewr.Core.Diff;

namespace CodeReviewr.Core.AI;

public enum AiRiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

public enum AiFileClassification
{
    Normal,
    ReviewCarefully,
    Skip,
}

public enum AiRunState
{
    Idle,
    Running,
    Incomplete,
    Complete,
    Failed,
    PausedBudget,
}

public enum AiRunStage
{
    Idle,
    Materialising,
    Connecting,
    Triaging,
    FileDepth,
    Chat,
    Cancelling,
    Done,
}

public enum AiAnnotationReadState
{
    Unread,
    Read,
    Dismissed,
}

public enum AiAnnotationSeverity
{
    Info,
    Suggestion,
    Warning,
    Risk,
}

public enum AiReviewScope
{
    PullRequest,
    WorkingCopyStaged,
    WorkingCopyAll,
}

/// <summary>Measured PR facts computed locally — never model judgements.</summary>
public sealed record AiMeasuredFacts(
    int FilesChanged,
    int LinesAdded,
    int LinesRemoved);

public sealed record AiRiskJustification(
    string FilePath,
    string Reason);

public sealed record AiFileTriage(
    string Path,
    AiFileClassification Classification,
    int PriorityStars,
    string? Guidance = null);

public sealed record AiPrTriageResult(
    string Summary,
    AiRiskLevel Risk,
    IReadOnlyList<AiRiskJustification> Justifications,
    IReadOnlyList<string> SuggestedOrder,
    IReadOnlyList<AiFileTriage> Files,
    AiMeasuredFacts Measured);

public sealed record AiFileSummaryResult(
    string Path,
    string Purpose,
    string InterestingChanges,
    string ReviewFocus);

public sealed record AiAnnotationResult(
    string Id,
    string Path,
    string BlobOid,
    int StartLine,
    int EndLine,
    DiffSide Side,
    AiAnnotationSeverity Severity,
    string Body,
    AiAnnotationReadState ReadState);

public sealed record AiRunProgress(
    AiRunStage Stage,
    int TurnsUsed,
    int? TurnBudget,
    int FilesCompleted,
    int FilesTotal,
    TimeSpan Elapsed,
    string? Message = null);

public sealed record AiRunSnapshot(
    string RunId,
    string SessionKey,
    string HeadSha,
    string MergeBaseSha,
    AiRunState State,
    string? CopilotSessionId,
    int TurnsUsed,
    string? AdHocInstructions,
    AiPrTriageResult? Triage,
    string? ErrorMessage,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc);

public sealed record AiConnectionProbeResult(
    bool Succeeded,
    string Message,
    bool NeedsDedicatedToken = false);

public sealed record AiChatMessage(
    string Role,
    string Content,
    DateTimeOffset TimestampUtc);

/// <summary>Request to start or resume an AI review session.</summary>
public sealed record AiReviewRequest(
    string SessionKey,
    string RepositoryPath,
    string RepositoryKey,
    string HeadSha,
    string MergeBaseSha,
    string? Title,
    string? Body,
    string? Author,
    string? BaseBranch,
    string? HeadBranch,
    IReadOnlyList<AiChangedFileFact> ChangedFiles,
    string? AdHocInstructions = null,
    bool DiscardCached = false,
    bool Resume = false,
    AiReviewScope Scope = AiReviewScope.PullRequest);

public sealed record AiChangedFileFact(
    string Path,
    string ChangeKind,
    string? BeforeBlobOid,
    string? AfterBlobOid,
    int? LinesAdded = null,
    int? LinesRemoved = null);

public sealed record AiFileDepthRequest(
    string SessionKey,
    string Path,
    string? BeforeBlobOid,
    string? AfterBlobOid,
    bool IncludeAnnotations = true);

public sealed record AiQuestionRequest(
    string SessionKey,
    string? Path,
    string Question,
    string? SelectedLinesContext = null);

public sealed record AiInlineActionRequest(
    string SessionKey,
    string Path,
    string Action,
    string SelectedLinesContext,
    int? StartLine = null,
    int? EndLine = null);

using CodeReviewr.Core.Diff;

namespace CodeReviewr.Core.AI;

public enum AiRiskLevel
{
    Low,
    Medium,
    High,
    Critical,
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
    ChangeBriefing,
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

/// <summary>Measured change facts computed locally — never model judgements.</summary>
public sealed record AiMeasuredFacts(
    int FilesChanged,
    int LinesAdded,
    int LinesRemoved);

/// <summary>Change-level briefing produced by <c>submit_change_briefing</c>.</summary>
public sealed record AiChangeBriefingResult(
    string ExecutiveSummary,
    AiRiskLevel Risk,
    IReadOnlyList<string> RiskDrivers,
    IReadOnlyList<string> WhatChanged,
    IReadOnlyList<string> ReviewFocus,
    AiTestingStatus TestingStatus,
    IReadOnlyList<string> Dependencies,
    AiMeasuredFacts? Measured = null);

/// <summary>How well the change appears to be covered by tests.</summary>
public sealed record AiTestingStatus(
    string Summary,
    IReadOnlyList<string> Notes);

/// <summary>Per-file briefing produced by <c>submit_file_briefing</c>.</summary>
public sealed record AiFileBriefingResult(
    string Path,
    string Overview,
    AiChangeClassification Classification,
    IReadOnlyList<string> Findings,
    int? QualityScore = null,
    string? QualityRationale = null);

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
    AiChangeBriefingResult? ChangeBriefing,
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
    int? LinesRemoved = null,
    int? ChangePercent = null);

public sealed record AiFileDepthRequest(
    string SessionKey,
    string Path,
    string? BeforeBlobOid,
    string? AfterBlobOid,
    bool IncludeAnnotations = true,
    int? ChangePercent = null,
    int? LinesAdded = null,
    int? LinesRemoved = null);

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

namespace CodeReviewr.Core;

public sealed record DiffOptions(
    string Algorithm = "histogram",
    int ContextLines = 3,
    bool IgnoreAllSpace = false,
    bool IgnoreSpaceChange = false,
    bool IgnoreBlankLines = false,
    bool DetectRenames = true,
    bool DetectCopies = true)
{
    public static DiffOptions Default { get; } = new();
}

public sealed record AppSettings
{
    public string? GitExecutablePath { get; set; }
    public string Theme { get; set; } = "System";
    public double FontSize { get; set; } = 13;
    public DiffViewMode DefaultDiffMode { get; set; } = DiffViewMode.Unified;
    public bool IgnoreWhitespace { get; set; }
    public bool ShowWhitespace { get; set; }
    public string DiffAlgorithm { get; set; } = "histogram";
    public int ContextLines { get; set; } = 3;
    public int SyntaxHighlightingSizeCapBytes { get; set; } = 1_000_000;
    public int SyntaxHighlightingLineLengthCap { get; set; } = 10_000;
    public List<string> RecentRepositories { get; set; } = [];
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public double NavigatorWidth { get; set; } = 260;
    public double FileListWidth { get; set; } = 300;
    public bool NavigatorCollapsed { get; set; }

    /// <summary>Flat vs tree layout for File Status / Stash file lists.</summary>
    public FileListLayoutMode FileStatusListLayout { get; set; } = FileListLayoutMode.Flat;

    /// <summary>Flat vs tree layout for History file lists.</summary>
    public FileListLayoutMode HistoryFileListLayout { get; set; } = FileListLayoutMode.Flat;

    /// <summary>Flat vs tree layout for Pull Request file lists.</summary>
    public FileListLayoutMode PullRequestFileListLayout { get; set; } = FileListLayoutMode.Flat;

    /// <summary>
    /// Temporary diagnostics toggle: when true, every git subprocess is delayed by a random 1–5s
    /// before starting. Used to test UI responsiveness under slow AV / disk environments.
    /// </summary>
    public bool SimulateSlowGit { get; set; }

    /// <summary>
    /// Max parallel background diff prefetch operations (clamped 1–8). Default 4.
    /// </summary>
    public int DiffPrefetchConcurrency { get; set; } = 4;

    /// <summary>
    /// Maximum raw unified-diff patch size (bytes) that will be buffered from Git.
    /// Larger outputs fail with <see cref="DiffTooLargeException"/> instead of risking OOM.
    /// </summary>
    public int MaxDiffPatchBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Maximum entries retained in the content-addressed <c>MemoryDiffCache</c>.</summary>
    public int DiffCacheCapacity { get; set; } = 256;

    /// <summary>
    /// When false (default), AI features that would send repository content off-device are disabled.
    /// </summary>
    public bool AiAssistanceEnabled { get; set; }

    /// <summary>
    /// Optional dedicated Copilot token when the GitHub account token lacks Copilot access.
    /// Stored via <c>ITokenStore</c> under a dedicated key — not persisted in this JSON document.
    /// </summary>
    public bool AiUseDedicatedCopilotToken { get; set; }

    /// <summary>Optional model override; null/empty inherits Copilot CLI default.</summary>
    public string? AiModelOverride { get; set; }

    /// <summary>Optional reasoning effort for models that support it (low/medium/high/xhigh).</summary>
    public string? AiReasoningEffort { get; set; }

    /// <summary>User override for review rules. Empty uses built-in defaults.</summary>
    public string AiReviewRules { get; set; } = "";

    /// <summary>Per-review turn budget. Run pauses and asks when reached.</summary>
    public int AiTurnBudget { get; set; } = 100;

    /// <summary>
    /// Auto-generate a file briefing when the file's change percent is at least this value
    /// (and <see cref="AiFileBriefingMinLinesChanged"/> is also met).
    /// </summary>
    public int AiFileBriefingMinChangePercent { get; set; } = 25;

    /// <summary>
    /// Auto-generate a file briefing when (lines added + removed) is at least this value
    /// (and <see cref="AiFileBriefingMinChangePercent"/> is also met).
    /// </summary>
    public int AiFileBriefingMinLinesChanged { get; set; } = 10;

    /// <summary>
    /// Idle timeout per turn in seconds. The clock resets whenever the agent streams text
    /// or starts/finishes a tool; the turn is cancelled only after this long with no activity.
    /// </summary>
    public int AiTurnTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Optional wall-clock timeout for an entire AI review run, in seconds.
    /// <c>0</c> means unlimited (turn idle timeout and turn budget still apply).
    /// </summary>
    public int AiRunTimeoutSeconds { get; set; } = 0;

    /// <summary>User-extensible path denylist patterns (glob-like), added to built-in secret patterns.</summary>
    public List<string> AiPathDenylist { get; set; } = [];

    /// <summary>Repository keys excluded from AI (privacy opt-out).</summary>
    public List<string> AiExcludedRepositories { get; set; } = [];

    /// <summary>User acknowledged that repository content is sent to GitHub Copilot.</summary>
    public bool AiDisclosureAcknowledged { get; set; }

    /// <summary>Days to retain unused materialised tree exports before lazy cleanup.</summary>
    public int AiExportRetentionDays { get; set; } = 14;

    /// <summary>File-count threshold that triggers pre-flight confirmation before a run.</summary>
    public int AiLargePrFileThreshold { get; set; } = 30;

    /// <summary>Root folder scanned for local Git repositories (Phase 2).</summary>
    public string? DevelopmentFolder { get; set; }

    /// <summary>Maximum directory depth when scanning <see cref="DevelopmentFolder"/>.</summary>
    public int RepositoryScanDepth { get; set; } = 6;

    /// <summary>Directory names skipped during repository scan (case-insensitive).</summary>
    public List<string> RepositoryScanIgnore { get; set; } =
    [
        "node_modules",
        "bin",
        "obj",
        "target",
        "Pods",
        "DerivedData",
        ".venv",
        "vendor",
    ];

    /// <summary>Known GitHub Enterprise host URLs (e.g. https://github.example.com).</summary>
    public List<string> EnterpriseHostUrls { get; set; } = [];

    /// <summary>Connected GitHub accounts (metadata only — no tokens).</summary>
    public List<GitHubAccountSettings> Accounts { get; set; } = [];

    /// <summary>Local repository to GitHub account bindings.</summary>
    public List<RepositoryAccountBinding> RepositoryBindings { get; set; } = [];

    public DiffOptions ToDiffOptions() => new(
        Algorithm: DiffAlgorithm,
        ContextLines: ContextLines,
        IgnoreAllSpace: IgnoreWhitespace,
        IgnoreSpaceChange: false,
        IgnoreBlankLines: false);
}

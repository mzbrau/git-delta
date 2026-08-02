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
    /// Phase 3 wires this to the Copilot (or other) SDK.
    /// </summary>
    public bool AiAssistanceEnabled { get; set; }

    /// <summary>
    /// When true, AI prompts redact likely secrets (tokens, private keys) before leaving the device.
    /// </summary>
    public bool AiRedactSecrets { get; set; } = true;

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

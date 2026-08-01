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

    public DiffOptions ToDiffOptions() => new(
        Algorithm: DiffAlgorithm,
        ContextLines: ContextLines,
        IgnoreAllSpace: IgnoreWhitespace,
        IgnoreSpaceChange: false,
        IgnoreBlankLines: false);
}

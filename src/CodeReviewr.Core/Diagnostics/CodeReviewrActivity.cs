using System.Diagnostics;

namespace CodeReviewr.Core.Diagnostics;

/// <summary>BCL <see cref="ActivitySource"/> for CodeReviewr traces (exported via OTLP when configured).</summary>
public static class CodeReviewrActivity
{
    public const string SourceName = "CodeReviewr";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}

using System.Diagnostics;

namespace GitDelta.Core.Diagnostics;

/// <summary>BCL <see cref="ActivitySource"/> for GitDelta traces (exported via OTLP when configured).</summary>
public static class GitDeltaActivity
{
    public const string SourceName = "GitDelta";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}

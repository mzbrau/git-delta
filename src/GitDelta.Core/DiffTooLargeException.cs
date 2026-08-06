namespace GitDelta.Core;

/// <summary>
/// Thrown when a Git stdout stream exceeds the configured size guard (e.g. a multi-hundred-MB patch).
/// Callers should surface a soft failure rather than materialising the full output.
/// </summary>
public sealed class DiffTooLargeException : GitException
{
    public long LimitBytes { get; }
    public long ObservedBytes { get; }

    public DiffTooLargeException(long limitBytes, long observedBytes)
        : base(
            $"Diff output exceeded the {FormatBytes(limitBytes)} size limit "
            + $"({FormatBytes(observedBytes)} read). Narrow the file or reduce context lines.",
            exitCode: -1)
    {
        LimitBytes = limitBytes;
        ObservedBytes = observedBytes;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000)
            return $"{bytes / 1_000_000.0:0.#} MB";
        if (bytes >= 1_000)
            return $"{bytes / 1_000.0:0.#} KB";
        return $"{bytes} B";
    }
}

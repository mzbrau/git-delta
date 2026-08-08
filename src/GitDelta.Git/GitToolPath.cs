namespace GitDelta.Git;

/// <summary>
/// Augments <c>PATH</c> so GUI-launched processes can find companion tools (e.g. <c>git-lfs</c>)
/// that live in Homebrew / Git-for-Windows install dirs missing from the minimal GUI PATH.
/// </summary>
public static class GitToolPath
{
    /// <summary>
    /// Builds an augmented PATH string: known tool directories (that exist) are prepended when
    /// missing from the current PATH. Deduplicates case-insensitively on Windows.
    /// </summary>
    /// <param name="currentPath">Existing PATH value (may be null or empty).</param>
    /// <param name="gitExecutablePath">Resolved git executable, when known; its directory is prepended if missing.</param>
    /// <param name="directoryExists">Directory existence check (defaults to <see cref="Directory.Exists"/>).</param>
    /// <param name="extraCandidateDirectories">
    /// Optional override for platform default candidate dirs (used by tests). When null, platform defaults apply.
    /// </param>
    public static string Augment(
        string? currentPath,
        string? gitExecutablePath = null,
        Func<string, bool>? directoryExists = null,
        IEnumerable<string>? extraCandidateDirectories = null)
    {
        directoryExists ??= Directory.Exists;
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var existing = new List<string>();
        var seen = new HashSet<string>(comparer);
        if (!string.IsNullOrEmpty(currentPath))
        {
            foreach (var part in currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim().Trim('"');
                if (trimmed.Length == 0 || !seen.Add(trimmed))
                    continue;
                existing.Add(trimmed);
            }
        }

        var prepended = new List<string>();

        void TryPrepend(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            directory = directory.Trim().Trim('"');
            if (directory.Length == 0 || !directoryExists(directory) || !seen.Add(directory))
                return;

            prepended.Add(directory);
        }

        if (!string.IsNullOrWhiteSpace(gitExecutablePath))
        {
            try
            {
                var gitDir = Path.GetDirectoryName(Path.GetFullPath(gitExecutablePath.Trim()));
                TryPrepend(gitDir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore unusable override paths; fall through to candidate dirs.
            }
        }

        var candidates = extraCandidateDirectories ?? EnumerateDefaultCandidateDirectories();
        foreach (var candidate in candidates)
            TryPrepend(candidate);

        return string.Join(Path.PathSeparator, prepended.Concat(existing));
    }

    /// <summary>Applies <see cref="Augment"/> to the current process environment PATH.</summary>
    public static void ApplyToProcess(string? gitExecutablePath = null)
    {
        var current = Environment.GetEnvironmentVariable("PATH");
        var augmented = Augment(current, gitExecutablePath);
        if (!string.Equals(current, augmented, StringComparison.Ordinal))
            Environment.SetEnvironmentVariable("PATH", augmented);
    }

    internal static IEnumerable<string> EnumerateDefaultCandidateDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var rootVar in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            {
                var root = Environment.GetEnvironmentVariable(rootVar);
                if (string.IsNullOrWhiteSpace(root))
                    continue;

                yield return Path.Combine(root, "Git", "cmd");
                yield return Path.Combine(root, "Git", "bin");
                yield return Path.Combine(root, "Git", "mingw64", "bin");
            }

            yield break;
        }

        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
    }
}

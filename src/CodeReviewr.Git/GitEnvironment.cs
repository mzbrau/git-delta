using System.Text.RegularExpressions;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewr.Git;

/// <summary>
/// Resolves the `git` executable from an override path or the process PATH, verifies the
/// minimum supported version, and caches the result for the lifetime of the application.
/// </summary>
public sealed partial class GitEnvironment : IGitEnvironment
{
    private static readonly string[] CandidateNames = OperatingSystem.IsWindows()
        ? ["git.exe", "git.cmd", "git.bat"]
        : ["git"];

    private readonly IGitProcessRunner _runner;
    private readonly ILogger<GitEnvironment> _logger;
    private readonly SemaphoreSlim _detectLock = new(1, 1);

    private string? _overridePath;
    private GitExecutableInfo? _cached;

    public GitEnvironment(IGitProcessRunner runner, ILogger<GitEnvironment>? logger = null)
    {
        _runner = runner;
        _logger = logger ?? NullLogger<GitEnvironment>.Instance;
    }

    public GitExecutableInfo? Current => _cached;

    public void SetOverridePath(string? path)
    {
        _overridePath = string.IsNullOrWhiteSpace(path) ? null : path;
        _cached = null;
    }

    public async Task<GitExecutableInfo> DetectAsync(CancellationToken ct = default)
    {
        if (_cached is { } cached)
            return cached;

        await _detectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } cachedAfterLock)
                return cachedAfterLock;

            var candidatePath = ResolveCandidatePath();

            var result = await _runner.RunAsync(
                Environment.CurrentDirectory,
                ["--version"],
                new GitProcessOptions { ExecutableOverride = candidatePath },
                ct).ConfigureAwait(false);

            var version = ParseVersion(result.Stdout);
            if (!version.MeetsMinimum)
            {
                throw new GitException(
                    $"Git {version} was found at '{candidatePath}', but CodeReviewr requires Git {GitVersion.Minimum} or newer. " +
                    "Install a newer Git and, if needed, set the executable path override in Settings.");
            }

            _runner.SetExecutablePath(candidatePath);
            var info = new GitExecutableInfo(candidatePath, version);
            _cached = info;
            _logger.LogInformation("Resolved git {Path} ({Version})", candidatePath, version);
            return info;
        }
        finally
        {
            _detectLock.Release();
        }
    }

    private string ResolveCandidatePath()
    {
        if (_overridePath is { } overridePath)
        {
            if (File.Exists(overridePath))
                return overridePath;

            throw new GitException($"The configured Git executable path '{overridePath}' does not exist.");
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidateName in CandidateNames)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), candidateName);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new GitException(
            "Git was not found on the PATH. Install Git 2.30 or newer, or set the executable path override in Settings.");
    }

    internal static GitVersion ParseVersion(string versionOutput)
    {
        var match = VersionRegex().Match(versionOutput);
        if (!match.Success)
            throw new GitException($"Could not parse `git --version` output: '{versionOutput.Trim()}'.");

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        return new GitVersion(major, minor, patch, versionOutput.Trim());
    }

    [GeneratedRegex(@"git version (?<major>\d+)\.(?<minor>\d+)(\.(?<patch>\d+))?")]
    private static partial Regex VersionRegex();
}

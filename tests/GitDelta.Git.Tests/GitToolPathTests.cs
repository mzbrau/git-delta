using GitDelta.Git;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class GitToolPathTests
{
    [Test]
    public void Augment_Prepends_Existing_Candidate_Dirs_Missing_From_Path()
    {
        var homebrew = Path.Combine(Path.GetTempPath(), "gitdelta-toolpath-hb-" + Guid.NewGuid().ToString("N"));
        var missing = Path.Combine(Path.GetTempPath(), "gitdelta-toolpath-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(homebrew);
        try
        {
            var existing = string.Join(Path.PathSeparator, ["/usr/bin", "/bin"]);
            var result = GitToolPath.Augment(
                existing,
                gitExecutablePath: null,
                directoryExists: dir => dir == homebrew || dir is "/usr/bin" or "/bin",
                extraCandidateDirectories: [homebrew, missing]);

            var parts = result.Split(Path.PathSeparator);
            Assert.That(parts[0], Is.EqualTo(homebrew));
            Assert.That(parts, Does.Not.Contain(missing));
            Assert.That(parts, Is.EqualTo(new[] { homebrew, "/usr/bin", "/bin" }));
        }
        finally
        {
            Directory.Delete(homebrew, recursive: true);
        }
    }

    [Test]
    public void Augment_Prepends_Git_Executable_Directory()
    {
        var gitDir = Path.Combine(Path.GetTempPath(), "gitdelta-toolpath-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gitDir);
        try
        {
            var gitExe = Path.Combine(gitDir, OperatingSystem.IsWindows() ? "git.exe" : "git");
            File.WriteAllText(gitExe, "");

            var result = GitToolPath.Augment(
                "/usr/bin",
                gitExecutablePath: gitExe,
                directoryExists: Directory.Exists,
                extraCandidateDirectories: Array.Empty<string>());

            var parts = result.Split(Path.PathSeparator);
            Assert.That(parts[0], Is.EqualTo(Path.GetFullPath(gitDir)));
            Assert.That(parts[^1], Is.EqualTo("/usr/bin"));
        }
        finally
        {
            Directory.Delete(gitDir, recursive: true);
        }
    }

    [Test]
    public void Augment_Does_Not_Duplicate_Or_Reorder_Dirs_Already_On_Path()
    {
        var homebrew = "/opt/homebrew/bin";
        var existing = string.Join(Path.PathSeparator, ["/usr/bin", homebrew]);
        var result = GitToolPath.Augment(
            existing,
            gitExecutablePath: null,
            directoryExists: _ => true,
            extraCandidateDirectories: [homebrew, "/usr/local/bin"]);

        var parts = result.Split(Path.PathSeparator);
        Assert.That(parts, Is.EqualTo(new[] { "/usr/local/bin", "/usr/bin", homebrew }));
        Assert.That(parts.Count(p => string.Equals(p, homebrew, StringComparison.Ordinal)), Is.EqualTo(1));
    }

    [Test]
    public void Augment_Handles_Empty_Current_Path()
    {
        var result = GitToolPath.Augment(
            null,
            gitExecutablePath: null,
            directoryExists: dir => dir == "/opt/homebrew/bin",
            extraCandidateDirectories: ["/opt/homebrew/bin", "/usr/local/bin"]);

        Assert.That(result, Is.EqualTo("/opt/homebrew/bin"));
    }
}

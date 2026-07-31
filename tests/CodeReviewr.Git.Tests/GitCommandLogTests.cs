using CodeReviewr.Git;
using NUnit.Framework;

namespace CodeReviewr.Git.Tests;

public sealed class GitCommandLogTests
{
    [Test]
    public void Append_Truncates_Large_Stdout_And_Caps_Entries()
    {
        var log = new GitCommandLog();
        var huge = new string('x', GitCommandLog.MaxStreamChars + 500);

        for (var i = 0; i < GitCommandLog.MaxEntries + 25; i++)
        {
            log.Append(new GitCommandLogEntry(
                DateTimeOffset.UtcNow,
                "/tmp",
                $"git status {i}",
                ExitCode: 0,
                Stdout: i == GitCommandLog.MaxEntries + 24 ? huge : "",
                Stderr: ""));
        }

        Assert.That(log.Entries, Has.Count.EqualTo(GitCommandLog.MaxEntries));
        Assert.That(log.Entries[0].CommandLine, Does.Contain("25")); // oldest dropped
        var truncated = log.Entries.Last(e => e.Stdout.Length > 0);
        Assert.That(truncated.Stdout, Does.Contain("truncated"));
        Assert.That(truncated.Stdout.Length, Is.LessThan(huge.Length));
    }
}

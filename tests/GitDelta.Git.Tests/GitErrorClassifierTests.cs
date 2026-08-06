using GitDelta.Git;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class GitErrorClassifierTests
{
    [TestCase("fatal: Authentication failed for 'https://github.com/x/y.git'", true)]
    [TestCase("Permission denied (publickey).", true)]
    [TestCase("terminal prompts disabled", true)]
    [TestCase("error: something else went wrong", false)]
    public void IsAuthFailure_Detects_Markers(string stderr, bool expected) =>
        Assert.That(GitErrorClassifier.IsAuthFailure(stderr), Is.EqualTo(expected));

    [TestCase("fatal: Unable to create '/repo/.git/index.lock': File exists.", true)]
    [TestCase("error: index.lock is held", false)]
    [TestCase("Unable to create '/repo/.git/index.lock': File exists.", true)]
    public void IsIndexLocked_Requires_Lock_And_Contention_Markers(string stderr, bool expected) =>
        Assert.That(GitErrorClassifier.IsIndexLocked(stderr), Is.EqualTo(expected));
}

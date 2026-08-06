using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using GitDelta.Git;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class DiffStdoutLimitTests
{
    [Test]
    public async Task GetCommitPatchAsync_Passes_MaxStdoutBytes()
    {
        var runner = Substitute.For<IGitProcessRunner>();
        var gates = new RepositoryGateProvider(runner);
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { MaxDiffPatchBytes = 12345 });

        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<GitProcessOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var args = ci.ArgAt<IReadOnlyList<string>>(1);
                // Common-dir probe used by RepositoryGateProvider.
                if (args.Count > 0 && args[0] == "rev-parse")
                    return new GitCommandResult(0, "/tmp/repo/.git\n", "", false, false);
                return new GitCommandResult(0, "", "", false, false);
            });

        var history = new GitHistoryService(runner, gates, settings);
        await history.GetCommitPatchAsync(
            "/tmp/repo",
            new string('a', 40),
            FilePath.From("a.txt"),
            DiffOptions.Default);

        var patchCalls = runner.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IGitProcessRunner.RunAsync))
            .Select(c => c.GetArguments())
            .Where(a => a[1] is IReadOnlyList<string> args && args.Count > 0 && args[0] == "show")
            .ToList();

        Assert.That(patchCalls, Has.Count.EqualTo(1));
        var options = (GitProcessOptions?)patchCalls[0][2];
        Assert.That(options, Is.Not.Null);
        Assert.That(options!.MaxStdoutBytes, Is.EqualTo(12345));
    }

    [Test]
    public async Task GetStashPatchAsync_Passes_MaxStdoutBytes()
    {
        var runner = Substitute.For<IGitProcessRunner>();
        var gates = new RepositoryGateProvider(runner);
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { MaxDiffPatchBytes = 54321 });

        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<GitProcessOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var args = ci.ArgAt<IReadOnlyList<string>>(1);
                if (args.Count > 0 && args[0] == "rev-parse")
                    return new GitCommandResult(0, "/tmp/repo/.git\n", "", false, false);
                return new GitCommandResult(0, "diff --git", "", false, false);
            });

        var stash = new GitStashService(runner, gates, settings);
        await stash.GetStashPatchAsync(
            "/tmp/repo",
            0,
            FilePath.From("a.txt"),
            DiffOptions.Default);

        var patchCalls = runner.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IGitProcessRunner.RunAsync))
            .Select(c => c.GetArguments())
            .Where(a => a[1] is IReadOnlyList<string> args && args.Count > 0 && args[0] == "diff")
            .ToList();

        Assert.That(patchCalls, Has.Count.EqualTo(1));
        var options = (GitProcessOptions?)patchCalls[0][2];
        Assert.That(options, Is.Not.Null);
        Assert.That(options!.MaxStdoutBytes, Is.EqualTo(54321));
    }
}

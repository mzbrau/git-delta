using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Git;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.Git.Tests;

public sealed class SimulatedLatencyGitProcessRunnerTests
{
    [Test]
    public async Task RunAsync_When_Disabled_Does_Not_Sample_Delay_And_Forwards()
    {
        var inner = Substitute.For<IGitProcessRunner>();
        var expected = new GitCommandResult(0, "ok", "", false, false);
        inner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<GitProcessOptions?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { SimulateSlowGit = false });

        var sampled = 0;
        var sut = new SimulatedLatencyGitProcessRunner(inner, settings, () =>
        {
            sampled++;
            return 50;
        });

        var result = await sut.RunAsync("/repo", ["status"]);

        Assert.That(result, Is.SameAs(expected));
        Assert.That(sampled, Is.EqualTo(0));
        await inner.Received(1).RunAsync("/repo", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "status" })), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_When_Enabled_Uses_Sampled_Delay_Then_Forwards()
    {
        var inner = Substitute.For<IGitProcessRunner>();
        var expected = new GitCommandResult(0, "ok", "", false, false);
        inner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<GitProcessOptions?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { SimulateSlowGit = true });

        var sampled = 0;
        var sut = new SimulatedLatencyGitProcessRunner(inner, settings, () =>
        {
            sampled++;
            return 0; // zero so the test stays fast
        });

        var result = await sut.RunAsync("/repo", ["diff"]);

        Assert.That(result, Is.SameAs(expected));
        Assert.That(sampled, Is.EqualTo(1));
        await inner.Received(1).RunAsync("/repo", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "diff" })), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public void StartLongLived_When_Enabled_Samples_Delay_Then_Forwards()
    {
        var process = Substitute.For<ILongLivedGitProcess>();
        var inner = Substitute.For<IGitProcessRunner>();
        inner.StartLongLived(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(process);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { SimulateSlowGit = true });

        var sampled = 0;
        var sut = new SimulatedLatencyGitProcessRunner(inner, settings, () =>
        {
            sampled++;
            return 0;
        });

        var result = sut.StartLongLived("/repo", ["cat-file", "--batch"]);

        Assert.That(result, Is.SameAs(process));
        Assert.That(sampled, Is.EqualTo(1));
        inner.Received(1).StartLongLived("/repo", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "cat-file", "--batch" })), Arg.Any<CancellationToken>());
    }

    [Test]
    public void SetExecutablePath_Forwards_To_Inner()
    {
        var inner = Substitute.For<IGitProcessRunner>();
        inner.ExecutablePath.Returns("git");
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());

        var sut = new SimulatedLatencyGitProcessRunner(inner, settings, () => 0);
        sut.SetExecutablePath("/usr/bin/git");

        Assert.That(sut.ExecutablePath, Is.EqualTo("git"));
        inner.Received(1).SetExecutablePath("/usr/bin/git");
    }
}

using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Caching;
using GitDelta.Diff;
using GitDelta.Git;
using GitDelta.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace GitDelta.IntegrationTests;

public sealed class RemoteAndCloneTests
{
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDiffCache, MemoryDiffCache>();
        services.AddGitDeltaGit();
        services.AddGitDeltaDiff();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Clone_Then_FfOnly_Pull_Advances_Local()
    {
        using var upstream = RepositoryBuilder.Create()
            .WithFile("a.txt", "v1\n")
            .WithInitialCommit("init");
        var upstreamPath = upstream.Build();

        var localParent = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        var localPath = Path.Combine(localParent, "clone");
        Directory.CreateDirectory(localParent);

        try
        {
            await using var sp = BuildServices();
            await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
            var clone = sp.GetRequiredService<IGitCloneService>();
            var remotes = sp.GetRequiredService<IGitRemoteService>();

            await clone.CloneAsync(upstreamPath, localPath, progress: null);

            // Pin EOL on the clone (mirrors RepositoryBuilder) so Windows CI matches LF expectations.
            var runner = sp.GetRequiredService<IGitProcessRunner>();
            await runner.RunAsync(localPath, ["config", "core.autocrlf", "false"], new GitProcessOptions());
            await runner.RunAsync(localPath, ["config", "core.eol", "lf"], new GitProcessOptions());
            await runner.RunAsync(localPath, ["checkout", "--force", "HEAD"], new GitProcessOptions());

            Assert.That(Directory.Exists(Path.Combine(localPath, ".git")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(localPath, "a.txt")), Is.EqualTo("v1\n"));

            var remoteUrl = await remotes.GetRemoteUrlAsync(localPath);
            Assert.That(remoteUrl, Is.Not.Null.And.Contain(upstreamPath));

            File.WriteAllText(Path.Combine(upstreamPath, "a.txt"), "v2\n");
            upstream.RunGit("add", "-A");
            upstream.RunGit("-c", "user.email=test@example.com", "-c", "user.name=Test",
                "commit", "-m", "second");

            await remotes.PullAsync(localPath, PullMode.FfOnly, progress: null);

            Assert.That(File.ReadAllText(Path.Combine(localPath, "a.txt")), Is.EqualTo("v2\n"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(localParent))
                    Directory.Delete(localParent, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}

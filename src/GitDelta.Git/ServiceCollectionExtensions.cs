using GitDelta.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace GitDelta.Git;

/// <summary>Registers the `GitDelta.Git` implementations behind Core's abstractions.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the CliWrap-backed Git implementation. Phase 1 supports a single open repository at
    /// a time, so <see cref="IRepositoryGateProvider"/> and the services built on it are registered as
    /// application-lifetime singletons scoped to whichever repository is currently open.
    /// </summary>
    public static IServiceCollection AddGitDeltaGit(this IServiceCollection services)
    {
        services.AddSingleton<IGitCommandLog, GitCommandLog>();
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<GitProcessRunner>>();
            var log = sp.GetService<IGitCommandLog>();
            // Production DI asserts Git never starts on a UI SynchronizationContext.
            return new GitProcessRunner(logger, log, assertNoUiSyncContext: true);
        });
        services.AddSingleton<IGitProcessRunner>(sp =>
        {
            var inner = sp.GetRequiredService<GitProcessRunner>();
            var settings = sp.GetService<ISettingsStore>();
            // Integration / unit hosts may omit ISettingsStore; skip the temporary latency wrapper then.
            return settings is null
                ? inner
                : new SimulatedLatencyGitProcessRunner(inner, settings);
        });
        services.AddSingleton<IGitEnvironment, GitEnvironment>();
        services.AddSingleton<IRepositoryGateProvider, RepositoryGateProvider>();

        services.AddSingleton<IGitStatusService, GitStatusService>();
        services.AddSingleton<IGitDiffRawService, GitDiffRawService>();
        services.AddSingleton<IGitObjectReader, GitObjectReader>();
        services.AddSingleton<IGitStagingService, GitStagingService>();
        services.AddSingleton<IGitDiscardService, GitDiscardService>();
        services.AddSingleton<IGitCommitService, GitCommitService>();
        services.AddSingleton<IGitBranchService, GitBranchService>();
        services.AddSingleton<IGitRemoteService, GitRemoteService>();
        services.AddSingleton<IGitCloneService, GitCloneService>();
        services.AddSingleton<IGitConflictService, GitConflictService>();
        services.AddSingleton<IGitRebaseService, GitRebaseService>();
        services.AddSingleton<IGitStashService, GitStashService>();
        services.AddSingleton<IGitHistoryService, GitHistoryService>();
        services.AddSingleton<GitRepositoryWatcher>();
        services.AddSingleton<IRepositoryWatcher>(sp => sp.GetRequiredService<GitRepositoryWatcher>());
        services.AddSingleton<IFsmonitorService, FsmonitorService>();

        return services;
    }
}

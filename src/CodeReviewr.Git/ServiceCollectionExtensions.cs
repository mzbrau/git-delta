using CodeReviewr.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewr.Git;

/// <summary>Registers the `CodeReviewr.Git` implementations behind Core's abstractions.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the CliWrap-backed Git implementation. Phase 1 supports a single open repository at
    /// a time, so <see cref="IRepositoryGate"/> and the services built on it are registered as
    /// application-lifetime singletons scoped to whichever repository is currently open.
    /// </summary>
    public static IServiceCollection AddCodeReviewrGit(this IServiceCollection services)
    {
        services.AddSingleton<IGitProcessRunner, GitProcessRunner>();
        services.AddSingleton<IGitEnvironment, GitEnvironment>();
        services.AddSingleton<IRepositoryGate, RepositoryGate>();

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
        services.AddSingleton<IGitStashService, GitStashService>();
        services.AddSingleton<GitRepositoryWatcher>();

        return services;
    }
}

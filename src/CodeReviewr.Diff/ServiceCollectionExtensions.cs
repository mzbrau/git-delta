using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeReviewr.Diff;

/// <summary>
/// Composition root helper for the Diff project. <see cref="IGitDiffRawService"/> is not registered
/// here — it is Git-shaped and belongs to whatever composes <c>CodeReviewr.Git</c> (or a test double)
/// alongside this project.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeReviewrDiff(this IServiceCollection services)
    {
        services.TryAddSingleton<IDiffCache, MemoryDiffCache>();
        services.TryAddSingleton<IIntraLineDiffer, IntraLineDiffer>();
        services.TryAddSingleton<ISyntaxTokenService, SyntaxTokenService>();
        services.TryAddSingleton<IGitDiffService, GitDiffService>();
        return services;
    }
}

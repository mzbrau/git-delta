using GitDelta.Core.Abstractions;
using GitDelta.Core.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GitDelta.Diff;

/// <summary>
/// Composition root helper for the Diff project. <see cref="IGitDiffRawService"/> is not registered
/// here — it is Git-shaped and belongs to whatever composes <c>GitDelta.Git</c> (or a test double)
/// alongside this project.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGitDeltaDiff(this IServiceCollection services)
    {
        services.TryAddSingleton<IDiffCache>(sp =>
        {
            var capacity = sp.GetService<ISettingsStore>()?.Current.DiffCacheCapacity
                ?? MemoryDiffCache.DefaultCapacity;
            return new MemoryDiffCache(capacity);
        });
        services.TryAddSingleton<IIntraLineDiffer, IntraLineDiffer>();
        services.TryAddSingleton<ISyntaxTokenService, SyntaxTokenService>();
        services.TryAddSingleton<IGitDiffService, GitDiffService>();
        return services;
    }
}

using CodeReviewr.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewr.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeReviewrPersistence(this IServiceCollection services)
    {
        // Factory forces the parameterless ctor; the ITokenStore-wrapping ctor must not be DI-selected.
        services.AddSingleton<ITokenStore>(_ => new PlatformTokenStore());
        services.AddSingleton<IDisposableCacheStore, SqliteDisposableCacheStore>();
        services.AddSingleton<IDurableUserStore, SqliteDurableUserStore>();
        services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<IDurableUserStore>());
        services.AddSingleton<ILocalNotesStore>(sp => sp.GetRequiredService<IDurableUserStore>());
        services.AddSingleton<ILocalViewedStore>(sp => sp.GetRequiredService<IDurableUserStore>());
        return services;
    }
}

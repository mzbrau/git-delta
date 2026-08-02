using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewr.GitHub;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeReviewrGitHub(this IServiceCollection services)
    {
        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton<ICapabilityCache, CapabilityCache>();
        services.AddSingleton<IGitHubClient, GitHubClient>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IPullRequestService, PullRequestService>();
        return services;
    }
}

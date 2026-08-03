using CodeReviewr.AI.Agent;
using CodeReviewr.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewr.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeReviewrAI(this IServiceCollection services)
    {
        services.AddSingleton<AiPromptCatalog>();
        services.AddSingleton<PrFactsAssembler>();
        services.AddSingleton<ReviewTreeMaterialiser>();
        services.AddSingleton<AiWorkQueue>();
        services.AddSingleton<IAgentClient, CopilotAgentClient>();
        services.AddSingleton<IAIReviewService, AiReviewCoordinator>();
        return services;
    }

    /// <summary>
    /// Test registration that swaps in a scripted <see cref="FakeAgentClient"/> instead of the
    /// real Copilot CLI, so <see cref="AiReviewCoordinator"/> can be exercised end-to-end without a
    /// live agent process. Internal because <see cref="FakeAgentClient"/> and friends are internal;
    /// visible to <c>CodeReviewr.AI.Tests</c> via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static IServiceCollection AddCodeReviewrAIWithFakeAgent(
        this IServiceCollection services,
        Func<AgentSessionOptions, FakeAgentScript> scriptFactory)
    {
        services.AddSingleton<AiPromptCatalog>();
        services.AddSingleton<PrFactsAssembler>();
        services.AddSingleton<ReviewTreeMaterialiser>();
        services.AddSingleton<AiWorkQueue>();
        services.AddSingleton<IAgentClient>(_ => new FakeAgentClient(scriptFactory));
        services.AddSingleton<IAIReviewService, AiReviewCoordinator>();
        return services;
    }
}

using GitDelta.AI.Agent;
using GitDelta.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace GitDelta.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGitDeltaAI(this IServiceCollection services)
    {
        services.AddSingleton<AiPromptCatalog>();
        services.AddSingleton<PrFactsAssembler>();
        services.AddSingleton<ReviewTreeMaterialiser>();
        services.AddSingleton<WorkingCopyMaterialiser>();
        services.AddSingleton<AiWorkQueue>();
        services.AddSingleton<IAgentClient, CopilotAgentClient>();
        services.AddSingleton<IAIReviewService, AiReviewCoordinator>();
        services.AddSingleton<IAiCommitAssistService, AiCommitAssistService>();
        return services;
    }

    /// <summary>
    /// Test registration that swaps in a scripted <see cref="FakeAgentClient"/> instead of the
    /// real Copilot CLI, so <see cref="AiReviewCoordinator"/> can be exercised end-to-end without a
    /// live agent process. Internal because <see cref="FakeAgentClient"/> and friends are internal;
    /// visible to <c>GitDelta.AI.Tests</c> via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static IServiceCollection AddGitDeltaAIWithFakeAgent(
        this IServiceCollection services,
        Func<AgentSessionOptions, FakeAgentScript> scriptFactory)
    {
        services.AddSingleton<AiPromptCatalog>();
        services.AddSingleton<PrFactsAssembler>();
        services.AddSingleton<ReviewTreeMaterialiser>();
        services.AddSingleton<WorkingCopyMaterialiser>();
        services.AddSingleton<AiWorkQueue>();
        services.AddSingleton<IAgentClient>(_ => new FakeAgentClient(scriptFactory));
        services.AddSingleton<IAIReviewService, AiReviewCoordinator>();
        services.AddSingleton<IAiCommitAssistService, AiCommitAssistService>();
        return services;
    }
}

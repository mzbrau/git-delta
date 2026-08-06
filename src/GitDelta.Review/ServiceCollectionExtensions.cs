using GitDelta.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace GitDelta.Review;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGitDeltaReview(this IServiceCollection services)
    {
        services.AddSingleton<IRepositoryLocator, RepositoryLocator>();
        services.AddSingleton<LocalRepositoryLocator>();
        services.AddSingleton<IReviewTreeFactory, GitReviewTreeFactory>();
        services.AddSingleton<IPullRequestGitService, PullRequestGitService>();
        services.AddSingleton<IReviewService, ReviewService>();
        services.AddSingleton<CommentAnchorMapper>();
        services.AddSingleton<ReviewMutationExecutor>();
        services.AddSingleton<IReviewOutbox, ReviewOutbox>();
        services.AddSingleton<IReviewCommentService, ReviewCommentService>();
        services.AddSingleton<IReviewSessionStore, ReviewSessionStoreStub>();
        return services;
    }
}

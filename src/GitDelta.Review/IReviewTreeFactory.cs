using GitDelta.Core;

namespace GitDelta.Review;

public interface IReviewTreeFactory
{
    IReviewTree Create(string repoPath, CommitId commit);
}

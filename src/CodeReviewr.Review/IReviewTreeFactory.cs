using CodeReviewr.Core;

namespace CodeReviewr.Review;

public interface IReviewTreeFactory
{
    IReviewTree Create(string repoPath, CommitId commit);
}

using CodeReviewr.Core;
using CodeReviewr.GitHub;

namespace CodeReviewr.Review;

public sealed class ReviewSession
{
    public ReviewSession(
        string repositoryPath,
        PullRequestDetail detail,
        CommitId mergeBase,
        CommitId head,
        IReviewTree headTree,
        IReadOnlyList<(FilePath Path, ChangeKind Kind)> files)
    {
        RepositoryPath = repositoryPath;
        Detail = detail;
        MergeBase = mergeBase;
        Head = head;
        HeadTree = headTree;
        Files = files;
    }

    public string RepositoryPath { get; }
    public PullRequestDetail Detail { get; }
    public CommitId MergeBase { get; }
    public CommitId Head { get; }
    public IReviewTree HeadTree { get; }
    public IReadOnlyList<(FilePath Path, ChangeKind Kind)> Files { get; }
}

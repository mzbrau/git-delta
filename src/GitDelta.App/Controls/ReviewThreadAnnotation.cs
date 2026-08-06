using GitDelta.Core.Diff;
using GitDelta.Review;

namespace GitDelta.App.Controls;

public sealed class ReviewThreadAnnotation(ReviewThread thread) : IDiffAnnotation
{
    public ReviewThread Thread { get; } = thread;
    public AnnotationRange Range { get; } = thread.Anchor
        ?? throw new InvalidOperationException("Review thread has no resolved anchor.");

    public bool IsOutdated => Thread.IsOutdated;
    public bool IsResolved => Thread.IsResolved;
}

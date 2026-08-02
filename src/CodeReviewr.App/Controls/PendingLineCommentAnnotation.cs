using CodeReviewr.Core;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.App.Controls;

/// <summary>Provisional gutter marker for a draft / pending line comment.</summary>
public sealed class PendingLineCommentAnnotation : IDiffAnnotation
{
    public PendingLineCommentAnnotation(DiffSide side, int line, int? startLine, ContentId content)
    {
        var start = new DiffAnchor(side, content, startLine ?? line);
        var end = new DiffAnchor(side, content, line);
        Range = new AnnotationRange(start, end);
    }

    public AnnotationRange Range { get; }
}

using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;

namespace GitDelta.App.Controls;

/// <summary>Gutter marker for a saved local-only (non-GitHub) review comment on pending changes.</summary>
public sealed class LocalLineCommentAnnotation : IDiffAnnotation
{
    public LocalLineCommentAnnotation(LocalCommentRecord record, ContentId content)
    {
        Record = record;
        var start = new DiffAnchor(record.Side, content, record.StartLine);
        var end = new DiffAnchor(record.Side, content, record.EndLine);
        Range = new AnnotationRange(start, end);
    }

    public LocalCommentRecord Record { get; }
    public AnnotationRange Range { get; }

    public bool IsResolved => Record.IsResolved;
}

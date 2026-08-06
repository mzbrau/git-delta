using GitDelta.Core.AI;
using GitDelta.Core.Diff;

namespace GitDelta.App.Controls;

/// <summary>Gutter marker for an AI-generated line annotation (overlay only; never mutates diff data).</summary>
public sealed class AiLineAnnotation(AiAnnotationResult result, AnnotationRange range) : IDiffAnnotation
{
    public AiAnnotationResult Result { get; } = result;
    public AnnotationRange Range { get; } = range;

    public bool IsDismissed => Result.ReadState == AiAnnotationReadState.Dismissed;
    public bool IsUnread => Result.ReadState == AiAnnotationReadState.Unread;
}

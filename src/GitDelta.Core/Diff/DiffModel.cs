namespace GitDelta.Core.Diff;

public readonly record struct CharSpan(int Start, int Length);

public readonly record struct DiffAnchor(DiffSide Side, ContentId Content, int Line);

public readonly record struct AnnotationRange(DiffAnchor Start, DiffAnchor End);

public readonly record struct FileDiffKey(
    ContentId OldContent,
    ContentId NewContent,
    DiffOptions Options);

public sealed record DiffLine(
    DiffLineKind Kind,
    int? OldLine,
    int? NewLine,
    ReadOnlyMemory<char> Text,
    IReadOnlyList<CharSpan>? IntraLine = null);

public sealed record DiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    string Header,
    IReadOnlyList<DiffLine> Lines);

public sealed record FileDiff(
    DiffScope Scope,
    FilePath OldPath,
    FilePath NewPath,
    ChangeKind Change,
    ContentId OldContent,
    ContentId NewContent,
    bool IsBinary,
    IReadOnlyList<DiffHunk> Hunks,
    string RawPatch);

public enum DiffRowKind
{
    Context,
    Added,
    Removed,
    Padding,
    HunkHeader,
    Collapsed,
}

public sealed record DiffRow(
    DiffRowKind Kind,
    int? OldLineNumber,
    int? NewLineNumber,
    ReadOnlyMemory<char> LeftText,
    ReadOnlyMemory<char> RightText,
    IReadOnlyList<CharSpan>? LeftIntraLine,
    IReadOnlyList<CharSpan>? RightIntraLine,
    int HunkIndex,
    int LineIndexInHunk,
    bool IsCollapsedAnchor = false,
    int CollapsedCount = 0);

public interface IDiffAnnotation
{
    AnnotationRange Range { get; }
}

public interface IDiffAnnotationSource
{
    ValueTask<IReadOnlyList<IDiffAnnotation>> GetAsync(FileDiffKey key, CancellationToken ct);
}

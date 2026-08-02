using CodeReviewr.Core;

namespace CodeReviewr.App.Controls;

public sealed record LineCommentRequest(DiffSide Side, int Line, int? StartLine);

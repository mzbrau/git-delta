using GitDelta.Core;

namespace GitDelta.App.Controls;

public sealed record LineCommentRequest(DiffSide Side, int Line, int? StartLine);

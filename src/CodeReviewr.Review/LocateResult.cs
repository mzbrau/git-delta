namespace CodeReviewr.Review;

public sealed record LocateResult(
    bool Found,
    string? LocalPath,
    bool Ambiguous,
    IReadOnlyList<string>? Candidates);

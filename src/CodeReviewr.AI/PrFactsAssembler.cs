using System.Text;
using CodeReviewr.Core.AI;

namespace CodeReviewr.AI;

/// <summary>
/// Builds the plain-text "facts" block embedded in prompts, and computes the measured facts that
/// are trusted over whatever the model reports for the same fields (see <see cref="AiMeasuredFacts"/>).
/// </summary>
public sealed class PrFactsAssembler
{
    public AiMeasuredFacts ComputeMeasuredFacts(AiReviewRequest request)
    {
        var files = request.ChangedFiles;
        var added = files.Sum(f => f.LinesAdded ?? 0);
        var removed = files.Sum(f => f.LinesRemoved ?? 0);
        return new AiMeasuredFacts(files.Count, added, removed);
    }

    public string BuildFactsBlock(AiReviewRequest request, string? threadSummary = null)
    {
        var measured = ComputeMeasuredFacts(request);
        var sb = new StringBuilder();

        sb.AppendLine($"Title: {request.Title ?? "(no title)"}");
        sb.AppendLine($"Author: {request.Author ?? "(unknown)"}");
        sb.AppendLine($"Branch: {request.HeadBranch ?? "?"} -> {request.BaseBranch ?? "?"}");
        sb.AppendLine($"Head SHA: {request.HeadSha}");
        sb.AppendLine($"Merge-base SHA: {request.MergeBaseSha}");
        sb.AppendLine($"Files changed: {measured.FilesChanged} (+{measured.LinesAdded} / -{measured.LinesRemoved})");

        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            sb.AppendLine();
            sb.AppendLine("Description:");
            sb.AppendLine(request.Body);
        }

        sb.AppendLine();
        sb.AppendLine("Changed files:");
        foreach (var file in request.ChangedFiles)
            sb.AppendLine($"- {file.Path} [{file.ChangeKind}] (+{file.LinesAdded ?? 0} / -{file.LinesRemoved ?? 0})");

        if (!string.IsNullOrWhiteSpace(threadSummary))
        {
            sb.AppendLine();
            sb.AppendLine("Discussion so far:");
            sb.AppendLine(threadSummary);
        }

        return sb.ToString();
    }
}

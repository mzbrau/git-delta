using System.Collections.ObjectModel;
using CodeReviewr.Core.AI;

namespace CodeReviewr.App.ViewModels;

/// <summary>
/// Shared AI-review presentation logic used by both <see cref="ReviewViewModel"/> (pull requests)
/// and <see cref="PendingChangesReviewViewModel"/> (working-copy / File Status), so triage results
/// are applied to file lists and "important files" cards the same way in both surfaces.
/// </summary>
public static class AiReviewSessionHelpers
{
    /// <summary>Copies per-file triage fields (stars, classification, guidance) onto <paramref name="files"/>.</summary>
    public static void ApplyTriageToFiles(AiPrTriageResult? triage, IEnumerable<FileItemViewModel> files)
    {
        var byPath = triage?.Files.ToDictionary(f => f.Path, StringComparer.Ordinal)
                     ?? new Dictionary<string, AiFileTriage>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (byPath.TryGetValue(file.Path.Value, out var fileTriage))
            {
                file.AiPriorityStars = fileTriage.PriorityStars;
                file.AiClassification = fileTriage.Classification.ToString();
                file.AiGuidance = fileTriage.Guidance;
            }
            else
            {
                file.AiPriorityStars = 0;
                file.AiClassification = null;
                file.AiGuidance = null;
            }
        }
    }

    /// <summary>
    /// Rebuilds the "important files" highlight list (review-carefully or ≥4★ files) from a triage
    /// result, ordered by the model's suggested review order.
    /// </summary>
    public static void RebuildImportantFiles(AiPrTriageResult? triage, ObservableCollection<AiImportantFileItem> target)
    {
        target.Clear();
        if (triage is null)
            return;

        var reasons = triage.Justifications
            .GroupBy(j => j.FilePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.Ordinal);

        var orderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < triage.SuggestedOrder.Count; i++)
            orderIndex.TryAdd(triage.SuggestedOrder[i], i);

        var important = triage.Files
            .Where(f => f.Classification == AiFileClassification.ReviewCarefully || f.PriorityStars >= 4)
            .OrderBy(f => orderIndex.TryGetValue(f.Path, out var index) ? index : int.MaxValue)
            .ThenByDescending(f => f.PriorityStars)
            .ThenBy(f => f.Path, StringComparer.Ordinal);

        foreach (var file in important)
        {
            string label;
            if (!string.IsNullOrWhiteSpace(file.Guidance))
                label = TruncateLabel(file.Guidance);
            else if (reasons.TryGetValue(file.Path, out var reason) && !string.IsNullOrWhiteSpace(reason))
                label = TruncateLabel(reason);
            else if (file.Classification == AiFileClassification.ReviewCarefully)
                label = "Review carefully";
            else
                label = $"{file.PriorityStars}★ priority";

            target.Add(new AiImportantFileItem(file.Path, label));
        }
    }

    public static string TruncateLabel(string text, int maxLength = 120)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;
        return trimmed[..(maxLength - 1)].TrimEnd() + "…";
    }
}

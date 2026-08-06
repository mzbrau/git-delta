using System.Text.Json;
using CodeReviewr.Core.AI;

namespace CodeReviewr.AI;

/// <summary>
/// Parses Copilot <c>submit_magic_commit_plan</c> tool arguments into a <see cref="MagicCommitPlan"/>.
/// Tolerant of common alternate field names the model invents when no JSON schema is available.
/// </summary>
internal static class MagicCommitPlanParser
{
    public static MagicCommitPlan Parse(string argsJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;

        // Accept either { "commits": [...] } or a bare array.
        JsonElement commitsEl;
        if (root.ValueKind == JsonValueKind.Array)
            commitsEl = root;
        else if (root.TryGetProperty("commits", out var nested))
            commitsEl = nested;
        else
            throw new InvalidOperationException("Magic Commit plan JSON must contain a 'commits' array.");

        var entries = new List<MagicCommitPlanEntry>();
        var index = 0;
        foreach (var item in commitsEl.EnumerateArray())
        {
            index++;
            var message = ReadMessage(item);
            var hunkIds = ReadHunkIds(item);

            if (string.IsNullOrWhiteSpace(message))
                throw new InvalidOperationException($"Commit {index} is missing a message.");
            if (hunkIds.Count == 0)
                throw new InvalidOperationException($"Commit {index} has no hunk IDs.");

            entries.Add(new MagicCommitPlanEntry(message, hunkIds));
        }

        if (entries.Count == 0)
            throw new InvalidOperationException("Magic Commit plan contained no commits.");

        return new MagicCommitPlan(entries);
    }

    private static string ReadMessage(JsonElement item)
    {
        if (TryGetString(item, "message", out var message) || TryGetString(item, "Message", out message))
            return message.Trim();

        if (!TryGetString(item, "subject", out var subject) && !TryGetString(item, "Subject", out subject))
            return "";

        subject = subject.Trim();
        if (subject.Length == 0)
            return "";

        if ((TryGetString(item, "body", out var body) || TryGetString(item, "Body", out body))
            && !string.IsNullOrWhiteSpace(body))
            return subject + "\n\n" + body.Trim();

        return subject;
    }

    private static IReadOnlyList<string> ReadHunkIds(JsonElement item)
    {
        if (!TryGetProperty(item, "hunkIds", out var idsEl)
            && !TryGetProperty(item, "HunkIds", out idsEl)
            && !TryGetProperty(item, "hunk_ids", out idsEl))
            return [];

        if (idsEl.ValueKind != JsonValueKind.Array)
            return [];

        return idsEl.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static bool TryGetString(JsonElement item, string name, out string value)
    {
        if (TryGetProperty(item, name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }

    private static bool TryGetProperty(JsonElement item, string name, out JsonElement value) =>
        item.TryGetProperty(name, out value);
}

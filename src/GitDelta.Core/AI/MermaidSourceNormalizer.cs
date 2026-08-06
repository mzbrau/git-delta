namespace GitDelta.Core.AI;

/// <summary>Normalizes AI- or user-supplied Mermaid source for rendering and persistence.</summary>
public static class MermaidSourceNormalizer
{
    /// <summary>Returns null for blank input; strips accidental markdown fences from Mermaid source.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0)
            return null;

        var start = 0;
        if (lines[0].StartsWith("```", StringComparison.Ordinal))
            start = 1;

        var end = lines.Length;
        if (end > start && lines[end - 1].Trim() == "```")
            end--;

        if (end <= start)
            return null;

        var body = string.Join('\n', lines[start..end]).Trim();
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }
}

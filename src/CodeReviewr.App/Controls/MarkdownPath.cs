namespace CodeReviewr.App.Controls;

/// <summary>Path helpers for markdown file preview gating.</summary>
public static class MarkdownPath
{
    public static bool IsMarkdownPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }
}

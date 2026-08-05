using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Styling;

namespace CodeReviewr.App.Controls;

/// <summary>
/// Maps TextMate scope names to brushes for DiffViewer token painting.
/// Heuristic Dark+/Light+-style palette — not a full TextMate theme port.
/// </summary>
internal static class SyntaxScopePalette
{
    private static readonly ConcurrentDictionary<string, IBrush> BrushCache = new(StringComparer.Ordinal);

    public static IBrush? BrushForScope(string scope, ThemeVariant? theme)
    {
        if (string.IsNullOrEmpty(scope))
            return null;

        var dark = theme != ThemeVariant.Light;
        var s = scope;

        if (Contains(s, "comment") || Contains(s, "punctuation.definition.comment"))
            return Hex(dark ? "#6A9955" : "#008000");
        if (Contains(s, "string"))
            return Hex(dark ? "#CE9178" : "#A31515");
        if (Contains(s, "constant.numeric") || Contains(s, "number"))
            return Hex(dark ? "#B5CEA8" : "#098658");
        if (Contains(s, "keyword") || Contains(s, "storage.type") || Contains(s, "storage.modifier"))
            return Hex(dark ? "#569CD6" : "#0000FF");
        if (Contains(s, "entity.name.function") || Contains(s, "support.function"))
            return Hex(dark ? "#DCDCAA" : "#795E26");
        if (Contains(s, "entity.name.type") || Contains(s, "entity.name.class") || Contains(s, "support.type")
            || Contains(s, "support.class"))
            return Hex(dark ? "#4EC9B0" : "#267F99");
        if (Contains(s, "variable") || Contains(s, "entity.name.variable"))
            return Hex(dark ? "#9CDCFE" : "#001080");
        if (Contains(s, "constant.language") || Contains(s, "support.constant"))
            return Hex(dark ? "#569CD6" : "#0000FF");
        if (Contains(s, "punctuation") || Contains(s, "operator"))
            return Hex(dark ? "#D4D4D4" : "#000000");

        return null;
    }

    private static bool Contains(string scope, string fragment) =>
        scope.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static IBrush Hex(string hex) =>
        BrushCache.GetOrAdd(hex, static h => new SolidColorBrush(Color.Parse(h)));
}

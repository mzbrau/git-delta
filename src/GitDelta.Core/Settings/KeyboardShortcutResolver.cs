namespace GitDelta.Core.Settings;

/// <summary>Merges saved shortcut overrides with catalog defaults and matches key events.</summary>
public static class KeyboardShortcutResolver
{
    /// <summary>
    /// Effective gesture text per id (defaults filled for missing entries; invalid overrides ignored).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveEffective(KeyboardShortcutBindings? saved)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var def in KeyboardShortcutCatalog.All)
        {
            if (saved?.Bindings.TryGetValue(def.Id, out var overrideText) == true)
            {
                if (KeyboardShortcutGesture.TryParse(overrideText, out _))
                {
                    result[def.Id] = overrideText?.Trim() ?? "";
                    continue;
                }
            }

            result[def.Id] = def.DefaultGesture;
        }

        return result;
    }

    /// <summary>
    /// Returns the first matching shortcut id for the given chord, or null.
    /// Prefer longer/more-specific catalog order is stable; first match wins.
    /// </summary>
    public static string? Match(
        KeyboardShortcutBindings? saved,
        bool ctrl,
        bool shift,
        bool alt,
        string key,
        bool textEntryFocused)
    {
        var effective = ResolveEffective(saved);
        var keyNorm = KeyboardShortcutGesture.NormalizeKeyToken(key);

        foreach (var def in KeyboardShortcutCatalog.All)
        {
            if (!effective.TryGetValue(def.Id, out var text) || string.IsNullOrWhiteSpace(text))
                continue;

            if (!KeyboardShortcutGesture.TryParse(text, out var gesture) || gesture.IsEmpty)
                continue;

            if (!gesture.Matches(ctrl, shift, alt, keyNorm))
                continue;

            if (textEntryFocused)
            {
                var isModified = gesture.Ctrl || gesture.Alt;
                if (!isModified && !def.RequiresModifiedChordInTextEntry)
                    continue;
                if (!isModified)
                    continue;
            }

            return def.Id;
        }

        return null;
    }

    /// <summary>
    /// Also matches secondary keys that share an action (e.g. Down for NextFile when bound to J).
    /// Used for arrow aliases that are not separately configurable.
    /// </summary>
    public static string? MatchWithAliases(
        KeyboardShortcutBindings? saved,
        bool ctrl,
        bool shift,
        bool alt,
        string key,
        bool textEntryFocused)
    {
        var direct = Match(saved, ctrl, shift, alt, key, textEntryFocused);
        if (direct is not null)
            return direct;

        // Built-in aliases for file navigation when the primary single-letter binding is still default-like.
        if (ctrl || shift || alt || textEntryFocused)
            return null;

        var keyNorm = KeyboardShortcutGesture.NormalizeKeyToken(key);
        var effective = ResolveEffective(saved);

        if (keyNorm is "Down" or "Up")
        {
            var id = keyNorm == "Down" ? KeyboardShortcutIds.NextFile : KeyboardShortcutIds.PreviousFile;
            if (effective.TryGetValue(id, out var text)
                && KeyboardShortcutGesture.TryParse(text, out var g)
                && !g.IsEmpty
                && !g.Ctrl && !g.Shift && !g.Alt)
            {
                return id;
            }
        }

        return null;
    }

    public static IReadOnlyList<(string IdA, string IdB, string Gesture)> FindConflicts(KeyboardShortcutBindings? saved)
    {
        var effective = ResolveEffective(saved);
        var byGesture = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, text) in effective)
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (!KeyboardShortcutGesture.TryParse(text, out var g) || g.IsEmpty)
                continue;

            var key = g.Text;
            if (!byGesture.TryGetValue(key, out var list))
            {
                list = [];
                byGesture[key] = list;
            }

            list.Add(id);
        }

        var conflicts = new List<(string, string, string)>();
        foreach (var (gesture, ids) in byGesture)
        {
            if (ids.Count < 2)
                continue;
            for (var i = 0; i < ids.Count; i++)
            {
                for (var j = i + 1; j < ids.Count; j++)
                    conflicts.Add((ids[i], ids[j], gesture));
            }
        }

        return conflicts;
    }
}

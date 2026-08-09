namespace GitDelta.Core.Settings;

/// <summary>
/// Platform-agnostic key chord (Control-based app shortcuts). Empty <see cref="Text"/> means unbound.
/// </summary>
public readonly record struct KeyboardShortcutGesture(
    bool Ctrl,
    bool Shift,
    bool Alt,
    string Key)
{
    public string Text
    {
        get
        {
            if (string.IsNullOrEmpty(Key))
                return "";

            var parts = new List<string>(4);
            if (Ctrl) parts.Add("Ctrl");
            if (Shift) parts.Add("Shift");
            if (Alt) parts.Add("Alt");
            parts.Add(FormatKeyToken(Key));
            return string.Join('+', parts);
        }
    }

    public static string FormatKeyToken(string key)
    {
        var norm = NormalizeKeyToken(key);
        return norm switch
        {
            "Oem5" => "\\",
            "Oem2" => "/",
            _ => norm,
        };
    }

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    public bool Matches(bool ctrl, bool shift, bool alt, string key) =>
        !IsEmpty
        && Ctrl == ctrl
        && Shift == shift
        && Alt == alt
        && string.Equals(NormalizeKeyToken(Key), NormalizeKeyToken(key), StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? text, out KeyboardShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            gesture = default;
            return true; // unbound
        }

        var tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        var ctrl = false;
        var shift = false;
        var alt = false;
        string? key = null;

        foreach (var token in tokens)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
                continue;
            }

            if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
                continue;
            }

            if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Option", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
                continue;
            }

            // Meta/Cmd intentionally ignored for app shortcuts (Control convention).
            if (token.Equals("Meta", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Cmd", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Command", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Win", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (key is not null)
                return false;

            key = NormalizeKeyToken(token);
        }

        if (string.IsNullOrEmpty(key))
            return false;

        gesture = new KeyboardShortcutGesture(ctrl, shift, alt, key);
        return true;
    }

    public static KeyboardShortcutGesture Parse(string text) =>
        TryParse(text, out var g) ? g : throw new FormatException($"Invalid shortcut gesture: '{text}'.");

    public static string NormalizeKeyToken(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "";

        var t = key.Trim();
        return t.ToUpperInvariant() switch
        {
            "\\" or "OEM5" or "BACKSLASH" => "Oem5",
            "/" or "OEM2" or "SLASH" or "QUESTION" => "Oem2",
            "ESC" or "ESCAPE" => "Escape",
            "ENTER" or "RETURN" => "Enter",
            "UP" or "UPARROW" => "Up",
            "DOWN" or "DOWNARROW" => "Down",
            "LEFT" or "LEFTARROW" => "Left",
            "RIGHT" or "RIGHTARROW" => "Right",
            "SPACE" or "SPACEBAR" => "Space",
            "PAGEUP" => "PageUp",
            "PAGEDOWN" => "PageDown",
            _ when t.Length == 1 => t.ToUpperInvariant(),
            _ => char.ToUpperInvariant(t[0]) + t[1..],
        };
    }
}

using Avalonia.Input;
using GitDelta.Core.Settings;

namespace GitDelta.App.Services;

/// <summary>Maps Avalonia keys to <see cref="KeyboardShortcutGesture"/> key tokens.</summary>
public static class AvaloniaKeyMapper
{
    public static string ToKeyToken(Key key) => key switch
    {
        Key.Oem5 => "Oem5",
        Key.Oem2 => "Oem2",
        Key.Escape => "Escape",
        Key.Enter or Key.Return => "Enter",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Space => "Space",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Home => "Home",
        Key.End => "End",
        Key.Tab => "Tab",
        Key.Back => "Back",
        Key.Delete => "Delete",
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        Key.F1 => "F1",
        Key.F2 => "F2",
        Key.F3 => "F3",
        Key.F4 => "F4",
        Key.F5 => "F5",
        Key.F6 => "F6",
        Key.F7 => "F7",
        Key.F8 => "F8",
        Key.F9 => "F9",
        Key.F10 => "F10",
        Key.F11 => "F11",
        Key.F12 => "F12",
        _ => key.ToString(),
    };

    public static bool TryToAvaloniaKey(string keyToken, out Key key)
    {
        var norm = KeyboardShortcutGesture.NormalizeKeyToken(keyToken);
        key = norm switch
        {
            "Oem5" => Key.Oem5,
            "Oem2" => Key.Oem2,
            "Escape" => Key.Escape,
            "Enter" => Key.Enter,
            "Up" => Key.Up,
            "Down" => Key.Down,
            "Left" => Key.Left,
            "Right" => Key.Right,
            "Space" => Key.Space,
            "PageUp" => Key.PageUp,
            "PageDown" => Key.PageDown,
            "Home" => Key.Home,
            "End" => Key.End,
            "Tab" => Key.Tab,
            "Back" => Key.Back,
            "Delete" => Key.Delete,
            { Length: 1 } s when s[0] is >= 'A' and <= 'Z' => Key.A + (s[0] - 'A'),
            { Length: 1 } s when s[0] is >= '0' and <= '9' => Key.D0 + (s[0] - '0'),
            "F1" => Key.F1,
            "F2" => Key.F2,
            "F3" => Key.F3,
            "F4" => Key.F4,
            "F5" => Key.F5,
            "F6" => Key.F6,
            "F7" => Key.F7,
            "F8" => Key.F8,
            "F9" => Key.F9,
            "F10" => Key.F10,
            "F11" => Key.F11,
            "F12" => Key.F12,
            _ => Key.None,
        };
        return key != Key.None;
    }

    public static string FormatChord(KeyModifiers modifiers, Key key)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var token = ToKeyToken(key);
        // Ignore pure modifier key presses.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return "";

        return new KeyboardShortcutGesture(ctrl, shift, alt, token).Text;
    }
}

namespace GitDelta.Core.Settings;

/// <summary>
/// Persisted keyboard shortcut overrides. Missing keys inherit catalog defaults.
/// Empty string values mean unbound.
/// </summary>
public sealed class KeyboardShortcutBindings
{
    /// <summary>Map of <see cref="KeyboardShortcutIds"/> → gesture text (e.g. <c>Ctrl+Shift+P</c>).</summary>
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.Ordinal);

    public KeyboardShortcutBindings Clone() => new()
    {
        Bindings = new Dictionary<string, string>(Bindings, StringComparer.Ordinal),
    };

    public static KeyboardShortcutBindings CreateDefaults()
    {
        var b = new KeyboardShortcutBindings();
        foreach (var (id, gesture) in KeyboardShortcutCatalog.DefaultBindings)
            b.Bindings[id] = gesture;
        return b;
    }
}

namespace GitDelta.Core.Settings;

public sealed record KeyboardShortcutDefinition(
    string Id,
    string DisplayName,
    string Category,
    string DefaultGesture,
    bool RequiresModifiedChordInTextEntry = false);

/// <summary>Built-in shortcut catalog and default gestures.</summary>
public static class KeyboardShortcutCatalog
{
    public static IReadOnlyList<KeyboardShortcutDefinition> All { get; } =
    [
        new(KeyboardShortcutIds.ToggleDiffMode, "Toggle diff mode", "Diff", "Ctrl+\\", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.ToggleShowFullFile, "Show full file", "Diff", "Ctrl+Shift+L", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.ToggleIgnoreWhitespace, "Toggle ignore whitespace", "Diff", "Ctrl+Shift+W", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.ToggleNavigator, "Toggle navigator sidebar", "Layout", "Ctrl+B", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.ToggleFilePanel, "Toggle File Panel", "Layout", "Ctrl+Shift+B", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.ToggleFileListQueryMode, "Toggle filter / search mode", "Layout", "Ctrl+Alt+F", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.ToggleFileListLayout, "Toggle flat list / tree view", "Layout", "Ctrl+Shift+T", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.Push, "Push", "Remote", "Ctrl+Shift+P", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.Pull, "Pull", "Remote", "Ctrl+Shift+U", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.Fetch, "Fetch", "Remote", "Ctrl+Shift+F", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.ViewRemote, "View remote", "Remote", "Ctrl+Shift+G", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.RevealInFileManager, "Show in Finder / Explorer", "Remote", "Ctrl+Shift+R", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.QuickOpen, "Quick-open file", "Navigation", "Ctrl+T", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.SubmitReview, "Submit pending comment review", "Pull request", "Ctrl+Enter", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.FocusFileFilter, "Focus file filter", "Pull request", "Ctrl+F", RequiresModifiedChordInTextEntry: true),
        new(KeyboardShortcutIds.FocusFileFilterSlash, "Focus file filter (/)", "Pull request", "/"),
        new(KeyboardShortcutIds.NextFile, "Next file", "Pull request", "J"),
        new(KeyboardShortcutIds.PreviousFile, "Previous file", "Pull request", "K"),
        new(KeyboardShortcutIds.ToggleViewed, "Toggle viewed", "Pull request", "V"),
        new(KeyboardShortcutIds.NextThread, "Next comment thread", "Pull request", "N"),
        new(KeyboardShortcutIds.PreviousThread, "Previous comment thread", "Pull request", "P"),
        new(KeyboardShortcutIds.FocusCommentDraft, "Focus comment draft", "Pull request", "C"),
        new(KeyboardShortcutIds.EscapeDismiss, "Dismiss / clear draft / close thread", "Pull request", "Escape"),
    ];

    public static IReadOnlyDictionary<string, string> DefaultBindings { get; } =
        All.ToDictionary(d => d.Id, d => d.DefaultGesture, StringComparer.Ordinal);

    public static KeyboardShortcutDefinition? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
}

using GitDelta.Core.Settings;
using NUnit.Framework;
using JsonSettingsStore = GitDelta.Core.Settings.JsonSettingsStore;

namespace GitDelta.Core.Tests;

public sealed class KeyboardShortcutTests
{
    [Test]
    public void Parse_Formats_RoundTrip()
    {
        Assert.That(KeyboardShortcutGesture.TryParse("Ctrl+Shift+P", out var g), Is.True);
        Assert.That(g.Ctrl, Is.True);
        Assert.That(g.Shift, Is.True);
        Assert.That(g.Alt, Is.False);
        Assert.That(g.Key, Is.EqualTo("P"));
        Assert.That(g.Text, Is.EqualTo("Ctrl+Shift+P"));
    }

    [Test]
    public void Parse_Backslash_And_Slash()
    {
        Assert.That(KeyboardShortcutGesture.TryParse("Ctrl+\\", out var slash), Is.True);
        Assert.That(slash.Key, Is.EqualTo("Oem5"));
        Assert.That(slash.Text, Is.EqualTo("Ctrl+\\"));

        Assert.That(KeyboardShortcutGesture.TryParse("/", out var filter), Is.True);
        Assert.That(filter.Key, Is.EqualTo("Oem2"));
        Assert.That(filter.Text, Is.EqualTo("/"));
    }

    [Test]
    public void Parse_Empty_Means_Unbound()
    {
        Assert.That(KeyboardShortcutGesture.TryParse("", out var g), Is.True);
        Assert.That(g.IsEmpty, Is.True);
        Assert.That(g.Text, Is.EqualTo(""));
    }

    [Test]
    public void ResolveEffective_Fills_Defaults_For_Missing()
    {
        var saved = new KeyboardShortcutBindings();
        saved.Bindings[KeyboardShortcutIds.Push] = "Ctrl+Alt+P";

        var effective = KeyboardShortcutResolver.ResolveEffective(saved);
        Assert.That(effective[KeyboardShortcutIds.Push], Is.EqualTo("Ctrl+Alt+P"));
        Assert.That(effective[KeyboardShortcutIds.QuickOpen], Is.EqualTo("Ctrl+T"));
    }

    [Test]
    public void ResolveEffective_Ignores_Invalid_Override()
    {
        var saved = new KeyboardShortcutBindings();
        saved.Bindings[KeyboardShortcutIds.Push] = "Ctrl++";

        var effective = KeyboardShortcutResolver.ResolveEffective(saved);
        Assert.That(effective[KeyboardShortcutIds.Push], Is.EqualTo("Ctrl+Shift+P"));
    }

    [Test]
    public void Match_Respects_TextEntry_For_Unmodified_Letters()
    {
        var bindings = KeyboardShortcutBindings.CreateDefaults();
        Assert.That(
            KeyboardShortcutResolver.Match(bindings, false, false, false, "J", textEntryFocused: true),
            Is.Null);
        Assert.That(
            KeyboardShortcutResolver.Match(bindings, false, false, false, "J", textEntryFocused: false),
            Is.EqualTo(KeyboardShortcutIds.NextFile));
    }

    [Test]
    public void Match_Allows_Modified_Chords_In_TextEntry()
    {
        var bindings = KeyboardShortcutBindings.CreateDefaults();
        Assert.That(
            KeyboardShortcutResolver.Match(bindings, true, false, false, "T", textEntryFocused: true),
            Is.EqualTo(KeyboardShortcutIds.QuickOpen));
    }

    [Test]
    public void MatchWithAliases_Maps_Arrows_To_File_Nav()
    {
        var bindings = KeyboardShortcutBindings.CreateDefaults();
        Assert.That(
            KeyboardShortcutResolver.MatchWithAliases(bindings, false, false, false, "Down", textEntryFocused: false),
            Is.EqualTo(KeyboardShortcutIds.NextFile));
        Assert.That(
            KeyboardShortcutResolver.MatchWithAliases(bindings, false, false, false, "Up", textEntryFocused: false),
            Is.EqualTo(KeyboardShortcutIds.PreviousFile));
    }

    [Test]
    public void FindConflicts_Detects_Duplicate_Gestures()
    {
        var saved = KeyboardShortcutBindings.CreateDefaults();
        saved.Bindings[KeyboardShortcutIds.Push] = "Ctrl+T";

        var conflicts = KeyboardShortcutResolver.FindConflicts(saved);
        Assert.That(conflicts.Any(c =>
            (c.IdA == KeyboardShortcutIds.Push || c.IdB == KeyboardShortcutIds.Push)
            && (c.IdA == KeyboardShortcutIds.QuickOpen || c.IdB == KeyboardShortcutIds.QuickOpen)), Is.True);
    }

    [Test]
    public async Task Shortcuts_RoundTrip_Through_Settings_Store()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new JsonSettingsStore(path);
            store.Update(s =>
            {
                s.Shortcuts.Bindings[KeyboardShortcutIds.Fetch] = "Ctrl+Alt+F";
                s.Shortcuts.Bindings[KeyboardShortcutIds.Push] = "";
            });
            await store.SaveAsync();

            var loaded = new JsonSettingsStore(path);
            loaded.Load();
            Assert.That(loaded.Current.Shortcuts.Bindings[KeyboardShortcutIds.Fetch], Is.EqualTo("Ctrl+Alt+F"));
            Assert.That(loaded.Current.Shortcuts.Bindings[KeyboardShortcutIds.Push], Is.EqualTo(""));

            var effective = KeyboardShortcutResolver.ResolveEffective(loaded.Current.Shortcuts);
            Assert.That(effective[KeyboardShortcutIds.Fetch], Is.EqualTo("Ctrl+Alt+F"));
            Assert.That(effective[KeyboardShortcutIds.Push], Is.EqualTo(""));
            Assert.That(effective[KeyboardShortcutIds.QuickOpen], Is.EqualTo("Ctrl+T"));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

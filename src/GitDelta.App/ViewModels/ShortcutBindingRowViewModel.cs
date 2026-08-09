using CommunityToolkit.Mvvm.ComponentModel;
using GitDelta.Core.Settings;

namespace GitDelta.App.ViewModels;

public sealed partial class ShortcutBindingRowViewModel : ObservableObject
{
    public ShortcutBindingRowViewModel(KeyboardShortcutDefinition definition, string gesture)
    {
        Id = definition.Id;
        DisplayName = definition.DisplayName;
        Category = definition.Category;
        DefaultGesture = definition.DefaultGesture;
        _gesture = gesture;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public string DefaultGesture { get; }

    [ObservableProperty] private string _gesture;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string? _conflictHint;

    public string GestureDisplay => string.IsNullOrWhiteSpace(Gesture) ? "(unbound)" : Gesture;

    partial void OnGestureChanged(string value) => OnPropertyChanged(nameof(GestureDisplay));
}

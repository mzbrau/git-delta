using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitDelta.Core.AI;
using LiveMarkdown.Avalonia;

namespace GitDelta.App.Controls;

/// <summary>
/// Hosts a Mermaid diagram via <see cref="MermaidPresenter"/>, with optional pan/zoom and
/// click-to-expand into a larger modal.
/// </summary>
public sealed class MermaidDiagramView : UserControl
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<MermaidDiagramView, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsInteractiveProperty =
        AvaloniaProperty.Register<MermaidDiagramView, bool>(nameof(IsInteractive), defaultValue: false);

    public static readonly StyledProperty<bool> ExpandOnClickProperty =
        AvaloniaProperty.Register<MermaidDiagramView, bool>(nameof(ExpandOnClick), defaultValue: false);

    private readonly MermaidPresenter _presenter = new();
    private readonly PanAndZoom _panZoom = new() { FitToViewport = true };
    private readonly SelectableTextBlock _error = new()
    {
        FontSize = 11,
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
        Margin = new Thickness(0, 4, 0, 0),
    };
    private readonly Border _host;
    private Point? _pressOrigin;
    private bool _didDrag;

    public MermaidDiagramView()
    {
        _panZoom.Content = _presenter;
        _host = new Border
        {
            Child = _panZoom,
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var root = new DockPanel();
        DockPanel.SetDock(_error, Dock.Bottom);
        root.Children.Add(_error);
        root.Children.Add(_host);
        Content = root;

        _host.PointerPressed += OnHostPointerPressed;
        _host.PointerMoved += OnHostPointerMoved;
        _host.PointerReleased += OnHostPointerReleased;
        _host.PointerCaptureLost += (_, _) =>
        {
            _pressOrigin = null;
            _didDrag = false;
        };
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsInteractive
    {
        get => GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    public bool ExpandOnClick
    {
        get => GetValue(ExpandOnClickProperty);
        set => SetValue(ExpandOnClickProperty, value);
    }

    static MermaidDiagramView()
    {
        SourceProperty.Changed.AddClassHandler<MermaidDiagramView>((view, _) => view.ApplySource());
        IsInteractiveProperty.Changed.AddClassHandler<MermaidDiagramView>((view, _) => view.ApplyInteractionMode());
        ExpandOnClickProperty.Changed.AddClassHandler<MermaidDiagramView>((view, _) => view.ApplyInteractionMode());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyInteractionMode();
        ApplySource();
    }

    /// <summary>Opens the interactive enlarge modal when the current source is non-empty.</summary>
    public bool TryExpand()
    {
        var source = MermaidSourceNormalizer.Normalize(Source);
        if (source is null)
            return false;

        MermaidDiagramWindow.Show(this, source);
        return true;
    }

    private void ApplyInteractionMode()
    {
        // Pan/zoom only when interactive; card previews stay click-to-expand.
        _panZoom.IsHitTestVisible = IsInteractive;
        _presenter.IsHitTestVisible = IsInteractive;
        _host.Cursor = ExpandOnClick && !IsInteractive
            ? new Cursor(StandardCursorType.Hand)
            : new Cursor(StandardCursorType.Arrow);
        ToolTip.SetTip(_host, ExpandOnClick && !IsInteractive ? "Click to enlarge" : null);
    }

    private void ApplySource()
    {
        _error.IsVisible = false;
        _error.Text = null;

        var source = MermaidSourceNormalizer.Normalize(Source);
        if (source is null)
        {
            _presenter.Text = null;
            _host.IsVisible = false;
            return;
        }

        _host.IsVisible = true;
        try
        {
            _presenter.Text = source;
            // MermaidPresenter swallows parse errors into diagram state; surface a fallback if empty.
            if (string.IsNullOrWhiteSpace(source))
            {
                ShowError("Diagram source was empty.", source);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, source);
        }
    }

    private void ShowError(string message, string? source)
    {
        _host.IsVisible = false;
        _error.IsVisible = true;
        _error.Text = string.IsNullOrWhiteSpace(source)
            ? $"Could not render diagram: {message}"
            : $"Could not render diagram: {message}\n\n{source}";
    }

    private void OnHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ExpandOnClick || IsInteractive)
            return;

        if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed)
            return;

        _pressOrigin = e.GetPosition(_host);
        _didDrag = false;
        e.Pointer.Capture(_host);
    }

    private void OnHostPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressOrigin is null)
            return;

        var delta = e.GetPosition(_host) - _pressOrigin.Value;
        if (Math.Abs(delta.X) > 4 || Math.Abs(delta.Y) > 4)
            _didDrag = true;
    }

    private void OnHostPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var origin = _pressOrigin;
        var dragged = _didDrag;
        _pressOrigin = null;
        _didDrag = false;
        e.Pointer.Capture(null);

        if (!ExpandOnClick || IsInteractive || origin is null || dragged)
            return;

        TryExpand();
    }
}

/// <summary>Modal window hosting an interactive Mermaid diagram.</summary>
public sealed class MermaidDiagramWindow : Window
{
    private MermaidDiagramWindow(string source)
    {
        Title = "Diagram";
        Width = 900;
        Height = 640;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (Application.Current?.TryGetResource("ForgeBackgroundBrush", Application.Current.ActualThemeVariant, out var bg) == true
            && bg is IBrush background)
        {
            Background = background;
        }
        else
        {
            Background = Brushes.White;
        }

        var diagram = new MermaidDiagramView
        {
            Source = source,
            IsInteractive = true,
            ExpandOnClick = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 8),
        };
        close.Classes.Add("Ghost");
        close.Click += (_, _) => Close();

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(close, Dock.Top);
        root.Children.Add(close);
        root.Children.Add(diagram);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    public static void Show(Control owner, string source)
    {
        var window = new MermaidDiagramWindow(source);
        var top = TopLevel.GetTopLevel(owner) as Window;
        if (top is not null)
            window.Show(top);
        else
            window.Show();
    }
}

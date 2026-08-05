using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeReviewr.App.Controls;
using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.GitHub;
using CodeReviewr.Review;

namespace CodeReviewr.App.Views;

public partial class MainWindow : Window
{
    private bool _suppressSelectionSync;
    private bool _multiSelectModifiers;
    private bool _selectionSyncSubscribed;
    private bool _gitConsoleSubscribed;
    private bool _aiChatScrollSubscribed;
    private bool _aiChatRowSubscribed;
    private bool _aiProgressScrollSubscribed;
    private double _prAiSidePanelWidth = 320;
    private bool _wcAiChatScrollSubscribed;
    private bool _wcAiChatRowSubscribed;
    private bool _wcAiProgressScrollSubscribed;
    private double _wcAiSidePanelWidth = 320;
    private bool _inlineCommentLayoutHooked;
    private bool _wcInlineCommentLayoutHooked;
    private bool _syncingInlineCommentLayout;
    private bool _syncingWcInlineCommentLayout;
    private TextBox? _activeMentionComposer;

    public MainWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Opened += OnOpened;
        Activated += OnActivated;
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;
        KeyDown += OnWindowKeyDown;
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private void OnActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.NotifyWindowActivated();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (!_selectionSyncSubscribed)
        {
            vm.WorkingCopy.SelectionClearRequested += ClearFileStatusListSelection;
            vm.WorkingCopy.SelectionSyncRequested += ApplySelectionToListBoxes;
            vm.Review.SelectionClearRequested += ClearPrFileListSelection;
            _selectionSyncSubscribed = true;
        }

        if (!_gitConsoleSubscribed)
        {
            vm.GitConsole.LinesUpdated += ScrollGitConsoleToEnd;
            _gitConsoleSubscribed = true;
        }

        if (!_aiChatScrollSubscribed)
        {
            vm.Review.AiChatMessages.CollectionChanged += OnAiChatMessagesChanged;
            _aiChatScrollSubscribed = true;
        }

        if (!_aiProgressScrollSubscribed)
        {
            vm.Review.AiActivityLogUpdated += ScrollAiProgressToEnd;
            _aiProgressScrollSubscribed = true;
        }

        if (!_aiChatRowSubscribed)
        {
            vm.Review.PropertyChanged += OnReviewPropertyChangedForAiChat;
            _aiChatRowSubscribed = true;
        }

        if (!_wcAiChatScrollSubscribed)
        {
            vm.WorkingCopy.PendingReview.AiChatMessages.CollectionChanged += OnWcAiChatMessagesChanged;
            _wcAiChatScrollSubscribed = true;
        }

        if (!_wcAiProgressScrollSubscribed)
        {
            vm.WorkingCopy.PendingReview.AiActivityLogUpdated += ScrollWcAiProgressToEnd;
            _wcAiProgressScrollSubscribed = true;
        }

        if (!_wcAiChatRowSubscribed)
        {
            vm.WorkingCopy.PendingReview.PropertyChanged += OnWcReviewPropertyChangedForAiChat;
            _wcAiChatRowSubscribed = true;
        }

        SyncAiSidePanelWidth();
        SyncWcAiSidePanelWidth();

        vm.Review.FocusCommentDraftRequested += FocusPrCommentDraft;
        vm.Review.FocusFileFilterRequested += FocusPrFileFilter;
        vm.Review.ExpandedThreadChanged += SyncInlineCommentLayout;
        vm.Review.MentionPopupChanged += PositionMentionPopup;

        vm.WorkingCopy.PendingReview.ExpandedLocalCommentChanged += SyncWcInlineCommentLayout;
        vm.WorkingCopy.PendingReview.RequestScrollToSelectedAnnotation += ScrollWcToSelectedAnnotation;
        vm.WorkingCopy.PendingReview.FocusCommentDraftRequested += FocusWcCommentDraft;
        vm.WorkingCopy.DiffScrollRequested += ScrollWcDiffToLine;
        vm.Review.DiffScrollRequested += ScrollPrDiffToLine;

        if (!_inlineCommentLayoutHooked)
        {
            _inlineCommentLayoutHooked = true;
            if (this.FindControl<Border>("InlineCommentDraft") is { } draft)
                draft.PropertyChanged += OnInlineCardLayoutChanged;
            if (this.FindControl<Border>("InlineThreadCard") is { } card)
                card.PropertyChanged += OnInlineCardLayoutChanged;
            if (this.FindControl<Border>("InlineAiAnnotationCard") is { } aiCard)
                aiCard.PropertyChanged += OnInlineCardLayoutChanged;
            if (this.FindControl<DiffViewer>("PrDiffViewer") is { } viewer)
            {
                viewer.PropertyChanged += OnPrDiffViewerPropertyChanged;
                viewer.ViewportChanged += SyncInlineCommentLayout;
            }
        }

        if (!_wcInlineCommentLayoutHooked)
        {
            _wcInlineCommentLayoutHooked = true;
            if (this.FindControl<Border>("WcInlineLocalCommentCard") is { } wcCard)
                wcCard.PropertyChanged += OnWcInlineCardLayoutChanged;
            if (this.FindControl<DiffViewer>("WcDiffViewer") is { } wcViewer)
            {
                wcViewer.PropertyChanged += OnWcDiffViewerPropertyChanged;
                wcViewer.ViewportChanged += SyncWcInlineCommentLayout;
            }
        }
    }

    private void OnInlineCardLayoutChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty || e.Property == Visual.IsVisibleProperty)
            SyncInlineCommentLayout();
    }

    private void OnWcInlineCardLayoutChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty || e.Property == Visual.IsVisibleProperty)
            SyncWcInlineCommentLayout();
    }

    private void OnPrDiffViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
            SyncInlineCommentLayout();
    }

    private void OnWcDiffViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
            SyncWcInlineCommentLayout();
    }

    private void ScrollWcToSelectedAnnotation()
    {
        if (DataContext is not MainWindowViewModel vm ||
            this.FindControl<DiffViewer>("WcDiffViewer") is not { } viewer)
            return;

        if (vm.WorkingCopy.PendingReview.SelectedLocalCommentAnnotation is { } ann)
            viewer.ScrollToLine(ann.Range.End.Side, ann.Range.End.Line);

        SyncWcInlineCommentLayout();
    }

    private void ScrollWcDiffToLine(DiffSide side, int line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<DiffViewer>("WcDiffViewer") is { } viewer)
                viewer.ScrollToLine(side, line);
        }, DispatcherPriority.Loaded);
    }

    private void ScrollPrDiffToLine(DiffSide side, int line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<DiffViewer>("PrDiffViewer") is { } viewer)
                viewer.ScrollToLine(side, line);
        }, DispatcherPriority.Loaded);
    }

    private void FocusWcCommentDraft()
    {
        // Draft composer is docked; focus best-effort on the visible TextBox after layout.
        Dispatcher.UIThread.Post(() =>
        {
            // No named draft box for WC — leave focus to Avalonia default.
        }, DispatcherPriority.Loaded);
    }

    private void SyncWcInlineCommentLayout()
    {
        if (_syncingWcInlineCommentLayout)
            return;

        if (DataContext is not MainWindowViewModel vm ||
            this.FindControl<DiffViewer>("WcDiffViewer") is not { } viewer)
        {
            return;
        }

        _syncingWcInlineCommentLayout = true;
        try
        {
            if (vm.WorkingCopy.PendingReview.HasExpandedLocalComment &&
                this.FindControl<Border>("WcInlineLocalCommentCard") is { } card)
            {
                if (vm.WorkingCopy.PendingReview.SelectedLocalCommentAnnotation is { } ann)
                {
                    var side = ann.Range.End.Side == DiffSide.Old ? "LEFT" : "RIGHT";
                    var line = ann.Range.End.Line;
                    PositionWcInlineCard(vm, card, side, line, clampReserve: 220);
                    if (vm.WorkingCopy.ShowMarkdownPreviewPane)
                        viewer.ClearInlineInset();
                    else
                        ApplyInlineInset(viewer, side, line, card);
                    return;
                }

                if (vm.WorkingCopy.PendingReview.ExpandedFileLevelComment is not null)
                {
                    PositionFileCommentCard(viewer, card);
                    if (vm.WorkingCopy.ShowMarkdownPreviewPane)
                        viewer.ClearInlineInset();
                    else
                        ApplyFileCommentInset(viewer, card);
                    return;
                }
            }

            viewer.ClearInlineInset();
        }
        finally
        {
            _syncingWcInlineCommentLayout = false;
        }
    }

    private void PositionWcInlineCard(
        MainWindowViewModel vm,
        Border card,
        string? sideName,
        int line,
        double clampReserve)
    {
        var side = string.Equals(sideName, "LEFT", StringComparison.OrdinalIgnoreCase)
            ? DiffSide.Old
            : DiffSide.New;

        double left = 48;
        double top = 24;
        if (TryGetWcLineAnchorRect(vm, side, line, out var anchor, out var hostHeight, out var hostWidth))
        {
            left = Math.Max(8, anchor.X);
            top = Math.Max(8, anchor.Y);
            var maxTop = Math.Max(8, hostHeight - clampReserve);
            if (top > maxTop)
                top = maxTop;
            var maxWidth = Math.Max(280, hostWidth - left - 16);
            card.Width = Math.Min(520, maxWidth);
        }

        Canvas.SetLeft(card, left);
        Canvas.SetTop(card, top);
    }

    private bool TryGetWcLineAnchorRect(
        MainWindowViewModel vm,
        DiffSide side,
        int line,
        out Rect anchor,
        out double hostHeight,
        out double hostWidth)
    {
        anchor = default;
        hostHeight = 400;
        hostWidth = 800;

        if (this.FindControl<DiffViewer>("WcDiffViewer") is { } viewer)
        {
            hostHeight = viewer.Bounds.Height;
            hostWidth = viewer.Bounds.Width;
            return viewer.TryGetLineAnchorRect(side, line, out anchor);
        }

        return false;
    }

    private void FocusPrCommentDraft()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        // Prefer the draft/edit composer over a sticky reply mention target so @
        // autocomplete anchors to the box the user just opened.
        if (vm.Review.HasDraftCommentAnchor)
        {
            SyncInlineCommentLayout();
            if (this.FindControl<TextBox>("InlineCommentDraftBox") is { } inlineBox)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    inlineBox.Focus();
                    inlineBox.CaretIndex = inlineBox.Text?.Length ?? 0;
                }, DispatcherPriority.Input);
                return;
            }
        }

        if (vm.Review.IsEditingComment &&
            this.FindControl<TextBox>("SideThreadEditBox") is { } editBox)
        {
            Dispatcher.UIThread.Post(() =>
            {
                editBox.Focus();
                editBox.CaretIndex = editBox.Text?.Length ?? 0;
            }, DispatcherPriority.Input);
            return;
        }

        if (vm.Review.MentionTargetsReply ||
            (vm.Review.HasExpandedInlineThread || vm.Review.ShowSideThreadPanel) &&
            !string.IsNullOrEmpty(vm.Review.ReplyBody))
        {
            TextBox? replyBox = null;
            if (vm.Review.HasExpandedInlineThread &&
                this.FindControl<TextBox>("InlineThreadReplyBox") is { } inlineReply &&
                IsEffectivelyShown(inlineReply))
            {
                replyBox = inlineReply;
            }
            else if (vm.Review.ShowSideThreadPanel &&
                     this.FindControl<TextBox>("SideThreadReplyBox") is { } sideReply &&
                     IsEffectivelyShown(sideReply))
            {
                replyBox = sideReply;
            }

            if (replyBox is not null)
            {
                var box = replyBox;
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.CaretIndex = box.Text?.Length ?? 0;
                }, DispatcherPriority.Input);
            }
        }
    }

    private void SyncInlineCommentLayout()
    {
        if (_syncingInlineCommentLayout)
            return;

        if (DataContext is not MainWindowViewModel vm ||
            this.FindControl<DiffViewer>("PrDiffViewer") is not { } viewer)
        {
            return;
        }

        _syncingInlineCommentLayout = true;
        try
        {
            if (vm.Review.HasDraftCommentAnchor &&
                this.FindControl<Border>("InlineCommentDraft") is { } draft)
            {
                if (vm.Review.DraftCommentLine is null)
                {
                    PositionFileCommentCard(viewer, draft);
                    if (vm.Review.ShowMarkdownPreviewPane)
                        viewer.ClearInlineInset();
                    else
                        ApplyFileCommentInset(viewer, draft);
                }
                else
                {
                    PositionInlineCard(vm, draft, vm.Review.DraftCommentSide, vm.Review.DraftCommentLine.Value, clampReserve: 180);
                    if (vm.Review.ShowMarkdownPreviewPane)
                        viewer.ClearInlineInset();
                    else
                        ApplyInlineInset(viewer, vm.Review.DraftCommentSide, vm.Review.DraftCommentLine.Value, draft);
                }
                return;
            }

            if (vm.Review.HasExpandedInlineThread &&
                vm.Review.SelectedThread?.Anchor is { } range &&
                this.FindControl<Border>("InlineThreadCard") is { } card)
            {
                var side = range.End.Side == DiffSide.Old ? "LEFT" : "RIGHT";
                PositionInlineCard(vm, card, side, range.End.Line, clampReserve: 220);
                if (vm.Review.ShowMarkdownPreviewPane)
                    viewer.ClearInlineInset();
                else
                    ApplyInlineInset(viewer, side, range.End.Line, card);
                return;
            }

            if (vm.Review.HasExpandedAiAnnotation &&
                vm.Review.SelectedAiAnnotation is { } aiAnnotation &&
                this.FindControl<Border>("InlineAiAnnotationCard") is { } aiCard)
            {
                var side = aiAnnotation.Range.End.Side == DiffSide.Old ? "LEFT" : "RIGHT";
                var line = aiAnnotation.Range.End.Line;
                PositionInlineCard(vm, aiCard, side, line, clampReserve: 200);
                if (vm.Review.ShowMarkdownPreviewPane)
                    viewer.ClearInlineInset();
                else
                    ApplyInlineInset(viewer, side, line, aiCard);
                return;
            }

            viewer.ClearInlineInset();
        }
        finally
        {
            _syncingInlineCommentLayout = false;
        }
    }

    private void PositionInlineCard(
        MainWindowViewModel vm,
        Border card,
        string? sideName,
        int line,
        double clampReserve)
    {
        var side = string.Equals(sideName, "LEFT", StringComparison.OrdinalIgnoreCase)
            ? DiffSide.Old
            : DiffSide.New;

        double left = 48;
        double top = 24;
        if (TryGetPrLineAnchorRect(vm, side, line, out var anchor, out var hostHeight, out var hostWidth))
        {
            left = Math.Max(8, anchor.X);
            top = Math.Max(8, anchor.Y);
            var maxTop = Math.Max(8, hostHeight - clampReserve);
            if (top > maxTop)
                top = maxTop;
            var maxWidth = Math.Max(280, hostWidth - left - 16);
            card.Width = Math.Min(520, maxWidth);
        }

        Canvas.SetLeft(card, left);
        Canvas.SetTop(card, top);
    }

    private bool TryGetPrLineAnchorRect(
        MainWindowViewModel vm,
        DiffSide side,
        int line,
        out Rect anchor,
        out double hostHeight,
        out double hostWidth)
    {
        anchor = default;
        hostHeight = 400;
        hostWidth = 800;

        if (vm.Review.ShowMarkdownPreviewPane &&
            this.FindControl<MarkdownFilePreview>("PrMarkdownPreview") is { } preview)
        {
            hostHeight = preview.Bounds.Height;
            hostWidth = preview.Bounds.Width;
            if (preview.TryGetLineAnchorRect(side, line, out anchor))
                return true;
        }

        if (this.FindControl<DiffViewer>("PrDiffViewer") is { } viewer)
        {
            hostHeight = viewer.Bounds.Height;
            hostWidth = viewer.Bounds.Width;
            return viewer.TryGetLineAnchorRect(side, line, out anchor);
        }

        return false;
    }

    private static void PositionFileCommentCard(DiffViewer viewer, Border card)
    {
        double left = 48;
        double top = 8;
        if (viewer.TryGetFileCommentAnchorRect(out var anchor))
        {
            left = Math.Max(8, anchor.X);
            top = anchor.Y;
            var maxWidth = Math.Max(280, viewer.Bounds.Width - left - 16);
            card.Width = Math.Min(520, maxWidth);
        }

        Canvas.SetLeft(card, left);
        Canvas.SetTop(card, top);
    }

    private static double MeasureCardHeight(Border card)
    {
        var height = Math.Max(0, card.Bounds.Height);
        if (height <= 0)
            height = card.DesiredSize.Height;
        if (height <= 0)
            height = 160;
        return height;
    }

    private static void ApplyInlineInset(DiffViewer viewer, string? sideName, int line, Border card)
    {
        var side = string.Equals(sideName, "LEFT", StringComparison.OrdinalIgnoreCase)
            ? DiffSide.Old
            : DiffSide.New;
        if (!viewer.TryGetRowIndex(side, line, out var rowIndex))
        {
            viewer.ClearInlineInset();
            return;
        }

        viewer.InlineInsetAfterRowIndex = rowIndex;
        viewer.InlineInsetHeight = MeasureCardHeight(card) + 12;
    }

    private static void ApplyFileCommentInset(DiffViewer viewer, Border card)
    {
        viewer.InlineInsetAfterRowIndex = -1;
        viewer.InlineInsetHeight = MeasureCardHeight(card) + 12;
    }

    private void PositionMentionPopup()
    {
        if (DataContext is not MainWindowViewModel vm ||
            this.FindControl<Popup>("CommentMentionPopup") is not { } popup)
        {
            return;
        }

        if (!vm.Review.IsMentionPopupOpen)
        {
            popup.IsOpen = false;
            return;
        }

        var box = ResolveMentionComposer(vm);
        if (box is null)
        {
            popup.IsOpen = false;
            return;
        }

        popup.PlacementTarget = box;
        popup.Placement = PlacementMode.AnchorAndGravity;
        popup.PlacementAnchor = PopupAnchor.BottomLeft;
        popup.PlacementGravity = PopupGravity.Bottom;
        popup.PlacementRect = GetCaretPlacementRect(box);
        popup.IsOpen = true;
    }

    private TextBox? ResolveMentionComposer(MainWindowViewModel vm)
    {
        if (_activeMentionComposer is { } active && IsEffectivelyShown(active))
            return active;

        if (vm.Review.HasDraftCommentAnchor &&
            this.FindControl<TextBox>("InlineCommentDraftBox") is { } inlineBox &&
            IsEffectivelyShown(inlineBox))
            return inlineBox;

        if (vm.Review.IsEditingComment &&
            this.FindControl<TextBox>("SideThreadEditBox") is { } editBox &&
            IsEffectivelyShown(editBox))
            return editBox;

        if (vm.Review.MentionTargetsReply ||
            vm.Review.HasExpandedInlineThread ||
            vm.Review.ShowSideThreadPanel)
        {
            if (vm.Review.HasExpandedInlineThread &&
                this.FindControl<TextBox>("InlineThreadReplyBox") is { } inlineReply &&
                IsEffectivelyShown(inlineReply))
                return inlineReply;
            if (vm.Review.ShowSideThreadPanel &&
                this.FindControl<TextBox>("SideThreadReplyBox") is { } sideReply &&
                IsEffectivelyShown(sideReply))
                return sideReply;
        }

        return null;
    }

    private static bool IsEffectivelyShown(Control control) =>
        control.IsVisible && control.IsEffectivelyVisible;

    private static Rect GetCaretPlacementRect(TextBox box)
    {
        var caretIndex = Math.Clamp(box.CaretIndex, 0, box.Text?.Length ?? 0);
        if (box.FindDescendantOfType<TextPresenter>() is { } presenter)
        {
            Rect caretLocal;
            if (presenter.TextLayout is TextLayout layout)
                caretLocal = layout.HitTestTextPosition(caretIndex);
            else
                caretLocal = new Rect(0, 0, 1, Math.Max(16, box.FontSize + 4));

            if (presenter.TranslatePoint(caretLocal.Position, box) is { } topLeft)
                return new Rect(topLeft, new Size(Math.Max(1, caretLocal.Width), Math.Max(1, caretLocal.Height)));
        }

        // Fallback: near the top-left of the text box content area.
        return new Rect(8, 8, 1, Math.Max(16, box.FontSize + 4));
    }

    private void OnCommentDraftTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || DataContext is not MainWindowViewModel vm)
            return;
        _activeMentionComposer = box;
        var isReply = box.Name is "InlineThreadReplyBox" or "SideThreadReplyBox";
        vm.Review.HandleComposerTextInput(box.Text ?? "", box.CaretIndex, isReply);
        Dispatcher.UIThread.Post(PositionMentionPopup, DispatcherPriority.Background);
    }

    private void OnCommentDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.Review.IsMentionPopupOpen)
            return;

        switch (e.Key)
        {
            case Key.Down:
                vm.Review.MoveMentionSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.Review.MoveMentionSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                vm.Review.AcceptSelectedMention();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.Review.DismissMentionPopupCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnAiChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        if (DataContext is not MainWindowViewModel vm)
            return;

        if (vm.Review.SendAiChatCommand.CanExecute(null))
        {
            _ = vm.Review.SendAiChatCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }

    private void OnMentionCandidatePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: MentionableUser user } ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.Review.SelectMentionCommand.Execute(user);
        e.Handled = true;
    }

    private void OnMentionPopupClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel { Review.IsMentionPopupOpen: true } vm)
            vm.Review.DismissMentionPopupCommand.Execute(null);
    }

    private void OnFileOrUnplaceableThreadPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: ReviewThread thread } ||
            DataContext is not MainWindowViewModel vm)
            return;

        vm.Review.SelectedAnnotation = null;
        vm.Review.SelectedThread = thread;
        e.Handled = true;
    }

    private void FocusPrFileFilter()
    {
        if (this.FindControl<TextBox>("PrFileFilterBox") is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.Review.IsPullRequestMode)
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = vm.Review.SubmitCommentShortcutCommand.ExecuteAsync(null);
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            e.Handled = true;
            vm.Review.RequestFileFilterFocusCommand.Execute(null);
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None)
            return;

        if (IsTextEntryFocused())
            return;

        switch (e.Key)
        {
            case Key.Escape:
                if (vm.Review.IsMentionPopupOpen)
                {
                    e.Handled = true;
                    vm.Review.DismissMentionPopupCommand.Execute(null);
                }
                else if (vm.Review.HasDraftCommentAnchor || vm.Review.IsEditingComment)
                {
                    e.Handled = true;
                    vm.Review.ClearDraftCommentAnchorCommand.Execute(null);
                }
                else if (vm.Review.HasExpandedInlineThread || vm.Review.ShowSideThreadPanel)
                {
                    e.Handled = true;
                    vm.Review.ClearExpandedThreadCommand.Execute(null);
                }
                break;
            case Key.J:
            case Key.Down:
                e.Handled = true;
                vm.Review.SelectNextFileCommand.Execute(null);
                break;
            case Key.K:
            case Key.Up:
                e.Handled = true;
                vm.Review.SelectPreviousFileCommand.Execute(null);
                break;
            case Key.V:
                e.Handled = true;
                _ = vm.Review.ToggleSelectedViewedCommand.ExecuteAsync(null);
                break;
            case Key.N:
                e.Handled = true;
                vm.Review.SelectNextThreadCommand.Execute(null);
                break;
            case Key.P:
                e.Handled = true;
                vm.Review.SelectPreviousThreadCommand.Execute(null);
                break;
            case Key.C:
                e.Handled = true;
                vm.Review.FocusCommentDraftCommand.Execute(null);
                break;
            case Key.Oem2:
                e.Handled = true;
                vm.Review.RequestFileFilterFocusCommand.Execute(null);
                break;
        }
    }

    private bool IsTextEntryFocused()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Control focused)
            return false;

        return focused is TextBox or AutoCompleteBox;
    }

    private void ScrollGitConsoleToEnd()
    {
        if (!Vm.GitConsole.IsExpanded) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ScrollViewer>("GitConsoleScroll") is { } scroll)
                scroll.Offset = new Avalonia.Vector(scroll.Offset.X, double.MaxValue);
        }, DispatcherPriority.Background);
    }

    private void OnAiChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScrollAiChatToEnd();

    private void OnReviewPropertyChangedForAiChat(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReviewViewModel.ShowAiSidePanel)
            or nameof(ReviewViewModel.IsConversationSelected))
            SyncAiSidePanelWidth();
        else if (e.PropertyName == nameof(ReviewViewModel.IsAiFileBriefingTabSelected))
            ScrollAiChatToEnd();
    }

    private void SyncAiSidePanelWidth()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (this.FindControl<Grid>("PrDiffChatGrid") is not { } grid) return;
        if (grid.ColumnDefinitions.Count < 3) return;

        var column = grid.ColumnDefinitions[2];
        var show = vm.Review.ShowAiSidePanel && !vm.Review.IsConversationSelected;
        if (show)
        {
            var width = Math.Clamp(_prAiSidePanelWidth, 240, 560);
            column.MinWidth = 240;
            column.MaxWidth = 560;
            column.Width = new GridLength(width);
            ScrollAiChatToEnd();
        }
        else
        {
            if (column.Width.IsAbsolute && column.Width.Value >= 240)
                _prAiSidePanelWidth = Math.Clamp(column.Width.Value, 240, 560);
            column.MinWidth = 0;
            column.MaxWidth = 560;
            column.Width = new GridLength(0);
        }
    }

    private void ScrollAiChatToEnd()
    {
        if (DataContext is not MainWindowViewModel vm ||
            !vm.Review.ShowAiSidePanel || !vm.Review.IsAiChatTabSelected)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ScrollViewer>("AiChatScrollViewer") is { } scroll)
                scroll.Offset = new Avalonia.Vector(scroll.Offset.X, double.MaxValue);
        }, DispatcherPriority.Background);
    }

    private void ScrollAiProgressToEnd()
    {
        if (DataContext is not MainWindowViewModel vm || !vm.Review.ShowAiProgressDialog) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ScrollViewer>("AiProgressScrollViewer") is { } scroll)
                scroll.Offset = new Avalonia.Vector(scroll.Offset.X, double.MaxValue);
        }, DispatcherPriority.Background);
    }

    private void OnWcAiChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScrollWcAiChatToEnd();

    private void ScrollWcAiProgressToEnd()
    {
        if (DataContext is not MainWindowViewModel vm || !vm.WorkingCopy.PendingReview.ShowAiProgressDialog) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ScrollViewer>("WcAiProgressScrollViewer") is { } scroll)
                scroll.Offset = new Avalonia.Vector(scroll.Offset.X, double.MaxValue);
        }, DispatcherPriority.Background);
    }

    private void OnWcReviewPropertyChangedForAiChat(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PendingChangesReviewViewModel.ShowAiSidePanel)
            or nameof(PendingChangesReviewViewModel.IsCommentsSelected))
            SyncWcAiSidePanelWidth();
        else if (e.PropertyName == nameof(PendingChangesReviewViewModel.IsAiFileBriefingTabSelected))
            ScrollWcAiChatToEnd();
    }

    private void SyncWcAiSidePanelWidth()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (this.FindControl<Grid>("WcDiffChatGrid") is not { } grid) return;
        if (grid.ColumnDefinitions.Count < 3) return;

        var column = grid.ColumnDefinitions[2];
        var show = vm.WorkingCopy.PendingReview.ShowAiSidePanel
                   && !vm.WorkingCopy.PendingReview.IsCommentsSelected;
        if (show)
        {
            var width = Math.Clamp(_wcAiSidePanelWidth, 240, 560);
            column.MinWidth = 240;
            column.MaxWidth = 560;
            column.Width = new GridLength(width);
            ScrollWcAiChatToEnd();
        }
        else
        {
            if (column.Width.IsAbsolute && column.Width.Value >= 240)
                _wcAiSidePanelWidth = Math.Clamp(column.Width.Value, 240, 560);
            column.MinWidth = 0;
            column.MaxWidth = 560;
            column.Width = new GridLength(0);
        }
    }

    private void ScrollWcAiChatToEnd()
    {
        if (DataContext is not MainWindowViewModel vm ||
            !vm.WorkingCopy.PendingReview.ShowAiSidePanel || !vm.WorkingCopy.PendingReview.IsAiChatTabSelected)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ScrollViewer>("WcAiChatScrollViewer") is { } scroll)
                scroll.Offset = new Avalonia.Vector(scroll.Offset.X, double.MaxValue);
        }, DispatcherPriority.Background);
    }

    private void OnWcAiChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.WorkingCopy.PendingReview.CanSendAiChat) return;

        e.Handled = true;
        _ = vm.WorkingCopy.PendingReview.SendAiChatCommand.ExecuteAsync(null);
    }

    private void OnWcDiagramEnlargeClick(object? sender, RoutedEventArgs e) =>
        this.FindControl<MermaidDiagramView>("WcBriefingDiagram")?.TryExpand();

    private void OnPrDiagramEnlargeClick(object? sender, RoutedEventArgs e) =>
        this.FindControl<MermaidDiagramView>("PrBriefingDiagram")?.TryExpand();

    private void OnOpened(object? sender, EventArgs e)
    {
        if (Vm.WindowWidth >= 640) Width = Vm.WindowWidth;
        if (Vm.WindowHeight >= 480) Height = Vm.WindowHeight;
        ApplyColumnWidths();
        SyncAiSidePanelWidth();
        SyncWcAiSidePanelWidth();

        if (global::CodeReviewr.App.App.Services.GetService(typeof(AvaloniaConfirmDialog)) is AvaloniaConfirmDialog confirm)
            confirm.Owner = this;
        if (global::CodeReviewr.App.App.Services.GetService(typeof(AvaloniaStashDialog)) is AvaloniaStashDialog stashDialog)
            stashDialog.Owner = this;
        if (global::CodeReviewr.App.App.Services.GetService(typeof(AvaloniaReviewSubmitDialog)) is AvaloniaReviewSubmitDialog reviewSubmit)
            reviewSubmit.Owner = this;

        // Defer repo open so the window can paint first.
        Dispatcher.UIThread.Post(() => _ = Vm.TryOpenLastRepositoryAsync(), DispatcherPriority.Background);
        Dispatcher.UIThread.Post(() => _ = Vm.Review.RefreshInboxCommand.ExecuteAsync(null), DispatcherPriority.Background);
        Dispatcher.UIThread.Post(() => _ = Vm.EnsureRepositoryCatalogAsync(), DispatcherPriority.Background);
    }

    private void OnRepoSwitcherFlyoutOpened(object? sender, EventArgs e) =>
        _ = Vm.EnsureRepositoryCatalogAsync();

    private void OnRepositoryEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;

        if (RepoSwitcherButton.Flyout is FlyoutBase flyout)
            flyout.Hide();

        _ = Vm.SelectRepositoryCommand.ExecuteAsync(path);
    }

    private void ApplyColumnWidths()
    {
        if (MainColumns.ColumnDefinitions.Count < 5) return;
        MainColumns.ColumnDefinitions[0].Width = Vm.NavigatorColumnWidth;
        MainColumns.ColumnDefinitions[2].Width = Vm.FileListColumnWidth;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        Vm.WindowWidth = Width;
        Vm.WindowHeight = Height;
        Vm.PersistLayout();
    }

    private async void OnOpenRepository(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Git Repository",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await Vm.OpenRepositoryPathAsync(path);
    }

    private async void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            await Vm.OpenRepositoryPathAsync(path);
    }

    private void OnNotificationAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppNotification n })
        {
            n.Action?.Invoke();
            Vm.Notifications.Dismiss(n);
        }
    }

    private void OnNotificationDismiss(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppNotification n })
            Vm.Notifications.Dismiss(n);
    }

    private void OnNotificationCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppNotification n })
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        _ = clipboard.SetTextAsync(n.CopyText);
    }

    private void OnToggleWorkspace(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.WorkspaceExpanded = !Vm.WorkingCopy.WorkspaceExpanded;

    private void OnToggleBranches(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.BranchesExpanded = !Vm.WorkingCopy.BranchesExpanded;

    private void OnToggleStashes(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.StashesExpanded = !Vm.WorkingCopy.StashesExpanded;

    private void OnTogglePullRequests(object? sender, RoutedEventArgs e) =>
        Vm.Review.PullRequestsExpanded = !Vm.Review.PullRequestsExpanded;

    private void OnToggleNeedsMyReview(object? sender, RoutedEventArgs e) =>
        Vm.Review.NeedsMyReviewExpanded = !Vm.Review.NeedsMyReviewExpanded;

    private void OnToggleReviewed(object? sender, RoutedEventArgs e) =>
        Vm.Review.ReviewedExpanded = !Vm.Review.ReviewedExpanded;

    private void OnToggleMyPullRequests(object? sender, RoutedEventArgs e) =>
        Vm.Review.MyPullRequestsExpanded = !Vm.Review.MyPullRequestsExpanded;

    private void OnPullRequestSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (list.SelectedItem is CodeReviewr.GitHub.PullRequestSummary summary)
            _ = Vm.Review.SelectPullRequestCommand.ExecuteAsync(summary);
    }

    private void OnToggleStaged(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.StagedExpanded = !Vm.WorkingCopy.StagedExpanded;

    private void OnToggleUnstaged(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.UnstagedExpanded = !Vm.WorkingCopy.UnstagedExpanded;

    private void OnStashFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (TryHandleFolderSelection(list, e.AddedItems, isHistory: false))
            return;
        if (TryHandleSearchHitSelection(list, e.AddedItems))
            return;
        if (list.SelectedItem is FileListEntry { File: { } file, IsSearchGroup: false })
            Vm.WorkingCopy.SetFileSelection([file]);
        else if (list.SelectedItem is FileItemViewModel legacy)
            Vm.WorkingCopy.SetFileSelection([legacy]);
        else
            Vm.WorkingCopy.SetFileSelection([]);
    }

    private void OnHistoryCommitSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync) return;
        if (sender is not ListBox list) return;
        Vm.WorkingCopy.SelectCommit(list.SelectedItem as CommitInfo);
    }

    private void OnHistoryFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync) return;
        if (sender is not ListBox list) return;
        if (TryHandleFolderSelection(list, e.AddedItems, isHistory: true))
            return;
        if (TryHandleSearchHitSelection(list, e.AddedItems))
            return;
        if (list.SelectedItem is FileListEntry { File: { } file, IsSearchGroup: false, IsSearchHit: false })
            Vm.WorkingCopy.SetFileSelection([file]);
        else if (list.SelectedItem is FileItemViewModel legacy)
            Vm.WorkingCopy.SetFileSelection([legacy]);
        else
            Vm.WorkingCopy.SetFileSelection([]);
    }

    private void OnToggleNavigatorCollapsed(object? sender, RoutedEventArgs e)
    {
        Vm.IsNavigatorCollapsed = !Vm.IsNavigatorCollapsed;
        ApplyColumnWidths();
    }

    private void OnFileCheckClick(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: FileItemViewModel file })
        {
            e.Handled = true;
            _ = Vm.WorkingCopy.ToggleFileStagedCommand.ExecuteAsync(file);
        }
    }

    private void OnFileListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _multiSelectModifiers = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                                || e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                                || e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (!e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed
            || sender is not ListBox list)
            return;

        var source = e.Source as Control;
        while (source is not null && source is not ListBoxItem)
            source = source.GetVisualParent() as Control;

        if (source is not ListBoxItem { DataContext: FileItemViewModel file })
            return;

        if (list.SelectedItems?.Contains(file) == true)
            return;

        _suppressSelectionSync = true;
        try
        {
            if (!_multiSelectModifiers)
            {
                ClearPeerSelections(list);
                list.SelectedItems?.Clear();
            }

            list.SelectedItems?.Add(file);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        SyncFileSelection();
    }

    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync) return;

        if (sender is ListBox source)
        {
            if (TryHandleFolderSelection(source, e.AddedItems, isHistory: false))
                return;

            if (TryHandleSearchHitSelection(source, e.AddedItems))
                return;

            if (!_multiSelectModifiers)
            {
                _suppressSelectionSync = true;
                try
                {
                    ClearPeerSelections(source);
                }
                finally
                {
                    _suppressSelectionSync = false;
                }
            }
        }

        SyncFileSelection();
    }

    private void ClearPeerSelections(ListBox source)
    {
        if (!ReferenceEquals(source, StagedFileList))
            StagedFileList.SelectedItems?.Clear();
        if (!ReferenceEquals(source, UnstagedFileList))
            UnstagedFileList.SelectedItems?.Clear();
        if (!ReferenceEquals(source, ConflictedFileList))
            ConflictedFileList.SelectedItems?.Clear();
    }

    private void SyncFileSelection()
    {
        var selected = new List<FileItemViewModel>();
        CollectSelected(StagedFileList, selected);
        CollectSelected(UnstagedFileList, selected);
        CollectSelected(ConflictedFileList, selected);
        Vm.WorkingCopy.SetFileSelection(selected);
    }

    private void ClearFileStatusListSelection()
    {
        _suppressSelectionSync = true;
        try
        {
            StagedFileList.SelectedItems?.Clear();
            UnstagedFileList.SelectedItems?.Clear();
            ConflictedFileList.SelectedItems?.Clear();
            if (this.FindControl<ListBox>("StashFileList") is { } stashFiles)
                stashFiles.SelectedItems?.Clear();
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    private void ClearPrFileListSelection()
    {
        if (this.FindControl<ListBox>("PrFileList") is not { } prFiles)
            return;

        prFiles.SelectedItem = null;
        prFiles.SelectedItems?.Clear();
    }

    private void ApplySelectionToListBoxes()
    {
        _suppressSelectionSync = true;
        try
        {
            StagedFileList.SelectedItems?.Clear();
            UnstagedFileList.SelectedItems?.Clear();
            ConflictedFileList.SelectedItems?.Clear();
            if (this.FindControl<ListBox>("HistoryFileList") is { } historyFiles)
                historyFiles.SelectedItem = null;

            foreach (var file in Vm.WorkingCopy.SelectedFilesSnapshot)
            {
                if (Vm.WorkingCopy.IsHistoryMode)
                {
                    if (this.FindControl<ListBox>("HistoryFileList") is { } hf)
                    {
                        var historyMatch = FindEntryInList(hf, file);
                        if (historyMatch is not null)
                            hf.SelectedItem = historyMatch;
                    }
                    continue;
                }

                var list = file.IsConflicted ? ConflictedFileList
                    : file.IsStagedList ? StagedFileList
                    : UnstagedFileList;
                // Only select items that exist in the list — prevents phantom SelectedItems
                // when a stale History FileItemViewModel leaks into File Status sync.
                var match = FindEntryInList(list, file);
                if (match is not null)
                    list.SelectedItems?.Add(match);
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    private bool TryHandleFolderSelection(ListBox list, System.Collections.IList added, bool isHistory)
    {
        foreach (var item in added)
        {
            if (item is not FileListEntry { IsExpandable: true, FolderKey: { } key })
                continue;

            _suppressSelectionSync = true;
            try
            {
                list.SelectedItems?.Remove(item);
                if (ReferenceEquals(list.SelectedItem, item))
                    list.SelectedItem = null;
            }
            finally
            {
                _suppressSelectionSync = false;
            }

            if (isHistory)
                Vm.WorkingCopy.ToggleHistoryFolderCommand.Execute(key);
            else
                Vm.WorkingCopy.ToggleFileStatusFolderCommand.Execute(key);
            return true;
        }

        return false;
    }

    private bool TryHandleSearchHitSelection(ListBox list, System.Collections.IList added)
    {
        foreach (var item in added)
        {
            if (item is not FileListEntry
                {
                    IsSearchHit: true,
                    File: { } file,
                    HitSide: { } side,
                    HitLine: { } line
                })
            {
                continue;
            }

            Vm.WorkingCopy.SelectSearchHit(file, side, line);
            return true;
        }

        return false;
    }

    private static FileListEntry? FindEntryInList(ListBox? list, FileItemViewModel file)
    {
        if (list?.Items is null) return null;
        FileListEntry? hitFallback = null;
        foreach (var item in list.Items)
        {
            if (item is not FileListEntry { File: { } candidate } entry
                || !string.Equals(candidate.Path.Value, file.Path.Value, StringComparison.Ordinal)
                || candidate.IsStagedList != file.IsStagedList)
            {
                continue;
            }

            if (entry.IsFile || entry.IsSearchGroup)
                return entry;
            if (entry.IsSearchHit)
                hitFallback ??= entry;
        }

        return hitFallback;
    }

    private static FileItemViewModel? FindInList(ListBox? list, FileItemViewModel file)
    {
        return FindEntryInList(list, file)?.File;
    }

    private static void CollectSelected(ListBox? list, List<FileItemViewModel> into)
    {
        if (list?.SelectedItems is null) return;
        foreach (var item in list.SelectedItems)
        {
            if (item is FileListEntry { IsSearchGroup: true })
                continue;
            if (item is FileListEntry { File: { } file })
                into.Add(file);
            else if (item is FileItemViewModel legacy)
                into.Add(legacy);
        }
    }

    private void OnColumnSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        Vm.CaptureColumnWidthsFromGrid(MainColumns);
}

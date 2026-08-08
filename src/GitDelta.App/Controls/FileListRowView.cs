using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using GitDelta.App.Converters;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using Material.Icons;
using Material.Icons.Avalonia;

namespace GitDelta.App.Controls;

public sealed class FileListRowView : UserControl
{
    public static readonly StyledProperty<bool> ShowStageCheckboxProperty =
        AvaloniaProperty.Register<FileListRowView, bool>(nameof(ShowStageCheckbox));

    public static readonly StyledProperty<bool> StageCheckboxCheckedProperty =
        AvaloniaProperty.Register<FileListRowView, bool>(nameof(StageCheckboxChecked));

    public static readonly StyledProperty<bool> ShowViewedEyeProperty =
        AvaloniaProperty.Register<FileListRowView, bool>(nameof(ShowViewedEye));

    public static readonly StyledProperty<ICommand?> StageToggleCommandProperty =
        AvaloniaProperty.Register<FileListRowView, ICommand?>(nameof(StageToggleCommand));

    public static readonly DirectProperty<FileListRowView, bool> ShowViewedIndicatorProperty =
        AvaloniaProperty.RegisterDirect<FileListRowView, bool>(
            nameof(ShowViewedIndicator), o => o.ShowViewedIndicator);

    public static readonly DirectProperty<FileListRowView, FontWeight> NameFontWeightProperty =
        AvaloniaProperty.RegisterDirect<FileListRowView, FontWeight>(
            nameof(NameFontWeight), o => o.NameFontWeight);

    private bool _showViewedIndicator;
    private FontWeight _nameFontWeight = FontWeight.Normal;
    private FileItemViewModel? _subscribedFile;
    private readonly Border _folderRow;
    private readonly Border _fileRow;
    private readonly Border _searchHitRow;
    private readonly MaterialIcon _folderChevron;
    private readonly MaterialIcon _folderIcon;
    private readonly TextBlock _folderLabel;
    private readonly CheckBox _stageCheck;
    private readonly MaterialIcon _statusIcon;
    private readonly MaterialIcon _searchGroupChevron;
    private readonly MiddleEllipsisTextBlock _name;
    private readonly MaterialIcon _cacheTick;
    private readonly MaterialIcon _viewedEye;
    private readonly StackPanel _commentPanel;
    private readonly MaterialIcon _commentIcon;
    private readonly TextBlock _commentCount;
    private readonly MaterialIcon _aiClassIcon;
    private readonly ChangePercentPie _pie;
    private readonly StackPanel _lineStats;
    private readonly TextBlock _linesAdded;
    private readonly TextBlock _linesRemoved;
    private readonly TextBlock _hitLineNumber;
    private readonly TextBlock _hitSnippet;

    public FileListRowView()
    {
        _folderChevron = Icon(MaterialIconKind.ChevronRight, 14, 0.7);
        _folderLabel = new TextBlock
        {
            Classes = { "MonoPath" },
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.9,
        };
        _folderIcon = Icon(MaterialIconKind.FolderOutline, 14);

        _folderRow = new Border
        {
            Classes = { "FileListRow" },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    _folderChevron,
                    _folderIcon,
                    _folderLabel,
                },
            },
        };

        _stageCheck = new CheckBox
        {
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            MinWidth = 0,
        };
        _stageCheck.Click += OnStageCheckClick;
        var stageCheckHost = new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(0.7, 0.7),
            Child = _stageCheck,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _statusIcon = Icon(MaterialIconKind.Pencil, 14);
        _searchGroupChevron = Icon(MaterialIconKind.ChevronRight, 14, 0.7);
        _searchGroupChevron.IsVisible = false;
        _name = new MiddleEllipsisTextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _name.FontFamily = ThemeFontFamily("ForgeCodeFont");
        _name.FontSize = ThemeDouble("ForgeCodeFontSize", 12);

        _cacheTick = Icon(MaterialIconKind.CheckCircleOutline, 12, 0.55);
        ToolTip.SetTip(_cacheTick, "Diff cached");

        _viewedEye = Icon(MaterialIconKind.EyeOutline, 12, 0.7);
        ToolTip.SetTip(_viewedEye, "Viewed");

        _commentCount = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _commentIcon = Icon(MaterialIconKind.MessageOutline, 12, 0.75);
        _commentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _commentIcon, _commentCount },
        };

        _aiClassIcon = Icon(MaterialIconKind.HelpCircleOutline, 14);

        _pie = new ChangePercentPie { Width = 12, Height = 12 };

        _linesAdded = new TextBlock { FontSize = 10, FontWeight = FontWeight.SemiBold };
        _linesRemoved = new TextBlock { FontSize = 10, FontWeight = FontWeight.SemiBold };
        _lineStats = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _linesAdded, _linesRemoved },
        };

        ApplyThemeBrushes();

        var leading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _searchGroupChevron, stageCheckHost, _statusIcon },
        };
        Grid.SetColumn(leading, 0);

        Grid.SetColumn(_name, 1);

        var afterName = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _cacheTick, _viewedEye },
        };
        Grid.SetColumn(afterName, 2);

        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _commentPanel, _aiClassIcon, _pie, _lineStats },
        };
        Grid.SetColumn(trailing, 3);

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
            ColumnSpacing = 6,
            Children = { leading, _name, afterName, trailing },
        };

        _fileRow = new Border
        {
            Classes = { "FileListRow" },
            Child = grid,
        };

        _hitLineNumber = new TextBlock
        {
            Classes = { "MonoPath" },
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 28,
        };
        _hitSnippet = new TextBlock
        {
            Classes = { "MonoPath" },
            FontSize = 11,
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _searchHitRow = new Border
        {
            Classes = { "FileListRow" },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { _hitLineNumber, _hitSnippet },
            },
        };

        _folderRow[!MarginProperty] = new Binding(nameof(FileListEntry.IndentMargin));
        _fileRow[!MarginProperty] = new Binding(nameof(FileListEntry.IndentMargin));
        _searchHitRow[!MarginProperty] = new Binding(nameof(FileListEntry.IndentMargin));
        _folderLabel[!TextBlock.TextProperty] = new Binding(nameof(FileListEntry.Label));
        _name[!MiddleEllipsisTextBlock.TextProperty] = new Binding(nameof(FileListEntry.Label));
        _folderChevron[!MaterialIcon.KindProperty] = new Binding(nameof(FileListEntry.IsExpanded))
        {
            Converter = ForgeConverters.ChevronKind,
        };
        _searchGroupChevron[!MaterialIcon.KindProperty] = new Binding(nameof(FileListEntry.IsExpanded))
        {
            Converter = ForgeConverters.ChevronKind,
        };
        _hitLineNumber[!TextBlock.TextProperty] = new Binding(nameof(FileListEntry.HitLine));

        Content = new Panel { Children = { _folderRow, _fileRow, _searchHitRow } };
        DataContextChanged += (_, _) => OnEntryChanged();
    }

    public bool ShowStageCheckbox
    {
        get => GetValue(ShowStageCheckboxProperty);
        set => SetValue(ShowStageCheckboxProperty, value);
    }

    public bool StageCheckboxChecked
    {
        get => GetValue(StageCheckboxCheckedProperty);
        set => SetValue(StageCheckboxCheckedProperty, value);
    }

    public bool ShowViewedEye
    {
        get => GetValue(ShowViewedEyeProperty);
        set => SetValue(ShowViewedEyeProperty, value);
    }

    public ICommand? StageToggleCommand
    {
        get => GetValue(StageToggleCommandProperty);
        set => SetValue(StageToggleCommandProperty, value);
    }

    public bool ShowViewedIndicator
    {
        get => _showViewedIndicator;
        private set => SetAndRaise(ShowViewedIndicatorProperty, ref _showViewedIndicator, value);
    }

    public FontWeight NameFontWeight
    {
        get => _nameFontWeight;
        private set => SetAndRaise(NameFontWeightProperty, ref _nameFontWeight, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyThemeBrushes();
        if (_subscribedFile is not null)
        {
            // Status badge brush comes from a converter that also resolves against the
            // current theme; re-apply so it tracks light/dark switches.
            _statusIcon.Foreground = (IBrush?)ForgeConverters.StatusBadgeBrush.Convert(
                _subscribedFile.Kind, typeof(IBrush), null, System.Globalization.CultureInfo.CurrentCulture)
                ?? Brushes.Gray;
        }

        _pie.InvalidateVisual();
    }

    private void ApplyThemeBrushes()
    {
        var onSurfaceVariant = ThemeBrush("ForgeOnSurfaceVariantBrush");
        _folderIcon.Foreground = onSurfaceVariant;
        _cacheTick.Foreground = onSurfaceVariant;
        _viewedEye.Foreground = onSurfaceVariant;
        _commentCount.Foreground = onSurfaceVariant;
        _commentIcon.Foreground = onSurfaceVariant;
        _aiClassIcon.Foreground = ThemeBrush("ForgeAiAccentBrush");
        _pie.Fill = ThemeBrush("ForgePrimaryBrush");
        _pie.Track = ThemeBrush("ForgeOutlineVariantBrush");
        _linesAdded.Foreground = ThemeBrush("ForgeStatusAddedBrush");
        _linesRemoved.Foreground = ThemeBrush("ForgeStatusDeletedBrush");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowStageCheckboxProperty)
            UpdateStageCheckVisibility();
        else if (change.Property == StageCheckboxCheckedProperty)
        {
            _stageCheck.IsChecked = StageCheckboxChecked;
            ToolTip.SetTip(_stageCheck, StageCheckboxChecked ? "Unstage file" : "Stage file");
        }
        else if (change.Property == ShowViewedEyeProperty || change.Property == NameFontWeightProperty)
            UpdateDerivedChrome();
        else if (change.Property == ShowViewedIndicatorProperty)
            _viewedEye.IsVisible = ShowViewedIndicator;
        else if (change.Property == NameFontWeightProperty)
            _name.FontWeight = NameFontWeight;
    }

    private void OnEntryChanged()
    {
        if (_subscribedFile is not null)
            _subscribedFile.PropertyChanged -= OnFilePropertyChanged;

        if (DataContext is not FileListEntry entry)
        {
            _subscribedFile = null;
            _folderRow.IsVisible = false;
            _fileRow.IsVisible = false;
            _searchHitRow.IsVisible = false;
            return;
        }

        _folderRow.IsVisible = entry.IsFolder;
        _fileRow.IsVisible = entry.IsFile || entry.IsSearchGroup;
        _searchHitRow.IsVisible = entry.IsSearchHit;
        _searchGroupChevron.IsVisible = entry.IsSearchGroup;

        UpdateStageCheckVisibility();
        _stageCheck.IsChecked = StageCheckboxChecked;
        ToolTip.SetTip(_stageCheck, StageCheckboxChecked ? "Unstage file" : "Stage file");

        _subscribedFile = entry.File;
        if (_subscribedFile is not null)
        {
            _subscribedFile.PropertyChanged += OnFilePropertyChanged;
            _stageCheck.Tag = _subscribedFile;
            ApplyFile(_subscribedFile);
        }

        if (entry.IsSearchHit)
        {
            ApplyHitSnippet(entry);
        }

        UpdateDerivedChrome();
    }

    private void ApplyHitSnippet(FileListEntry entry)
    {
        var snippet = entry.HitSnippet ?? entry.Label;
        var inlines = _hitSnippet.Inlines;
        inlines?.Clear();
        _hitSnippet.Text = null;

        var matchIndex = entry.HitSnippetMatchIndex;
        var matchLength = entry.HitSnippetMatchLength;
        if (inlines is null
            || matchLength <= 0
            || matchIndex < 0
            || matchIndex >= snippet.Length
            || matchIndex + matchLength > snippet.Length)
        {
            _hitSnippet.Text = snippet;
            return;
        }

        if (matchIndex > 0)
            inlines.Add(new Run(snippet[..matchIndex]));

        inlines.Add(new Run(snippet[matchIndex..(matchIndex + matchLength)])
        {
            FontWeight = FontWeight.Bold,
        });

        var after = matchIndex + matchLength;
        if (after < snippet.Length)
            inlines.Add(new Run(snippet[after..]));
    }

    private void UpdateStageCheckVisibility()
    {
        var isSearchChrome = DataContext is FileListEntry { IsSearchGroup: true } or FileListEntry { IsSearchHit: true };
        _stageCheck.IsVisible = ShowStageCheckbox && !isSearchChrome;
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_subscribedFile is null)
            return;

        switch (e.PropertyName)
        {
            case nameof(FileItemViewModel.IsViewed):
                UpdateDerivedChrome();
                break;
            case nameof(FileItemViewModel.HasCachedDiff):
            case nameof(FileItemViewModel.IsDiffStale):
                _cacheTick.IsVisible = _subscribedFile.HasCachedDiff;
                break;
            case nameof(FileItemViewModel.LinesAdded):
            case nameof(FileItemViewModel.LinesRemoved):
            case nameof(FileItemViewModel.HasLineStats):
            case nameof(FileItemViewModel.ChangePercent):
            case nameof(FileItemViewModel.HasChangePercent):
            case nameof(FileItemViewModel.ChangePercentTooltip):
                ApplyCacheStatsChrome(_subscribedFile);
                break;
            default:
                ApplyFile(_subscribedFile);
                break;
        }
    }

    private void ApplyCacheStatsChrome(FileItemViewModel file)
    {
        _pie.IsVisible = file.HasChangePercent;
        _pie.Percent = file.ChangePercent;
        ToolTip.SetTip(_pie, file.ChangePercentTooltip);

        _lineStats.IsVisible = file.HasLineStats;
        var added = file.LinesAdded ?? 0;
        var removed = file.LinesRemoved ?? 0;
        _linesAdded.Text = $"+{added}";
        _linesRemoved.Text = $"-{removed}";
        ToolTip.SetTip(_lineStats, $"+{added} lines added, -{removed} lines removed");
    }

    private void ApplyFile(FileItemViewModel file)
    {
        _statusIcon.Kind = StatusIcon(file.Kind);
        _statusIcon.Foreground = (IBrush?)ForgeConverters.StatusBadgeBrush.Convert(
            file.Kind, typeof(IBrush), null, System.Globalization.CultureInfo.CurrentCulture)
            ?? Brushes.Gray;
        ToolTip.SetTip(_statusIcon, StatusTooltip(file.Kind));

        ToolTip.SetTip(_stageCheck, StageCheckboxChecked ? "Unstage file" : "Stage file");

        _cacheTick.IsVisible = file.HasCachedDiff;

        _commentPanel.IsVisible = file.TotalCommentCount > 0;
        _commentCount.Text = file.TotalCommentCount.ToString();
        ToolTip.SetTip(
            _commentPanel,
            file.TotalCommentCount == 1
                ? "1 comment"
                : $"{file.TotalCommentCount} comments");

        _aiClassIcon.IsVisible = file.HasAiChangeClassification;
        if (file.AiChangeClassification is { } classification)
        {
            _aiClassIcon.Kind = (MaterialIconKind)(ForgeConverters.AiChangeClassificationIcon.Convert(
                classification, typeof(MaterialIconKind), null, System.Globalization.CultureInfo.CurrentCulture)
                ?? MaterialIconKind.HelpCircleOutline);
            ToolTip.SetTip(_aiClassIcon, ForgeConverters.AiChangeClassificationTooltip.Convert(
                classification, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string);
        }

        _pie.IsVisible = file.HasChangePercent;
        _pie.Percent = file.ChangePercent;
        ToolTip.SetTip(_pie, file.ChangePercentTooltip);

        _lineStats.IsVisible = file.HasLineStats;
        var added = file.LinesAdded ?? 0;
        var removed = file.LinesRemoved ?? 0;
        _linesAdded.Text = $"+{added}";
        _linesRemoved.Text = $"-{removed}";
        ToolTip.SetTip(_lineStats, $"+{added} lines added, -{removed} lines removed");
    }

    private void UpdateDerivedChrome()
    {
        var viewed = _subscribedFile?.IsViewed == true;
        ShowViewedIndicator = ShowViewedEye && viewed;
        NameFontWeight = ShowViewedEye && !viewed ? FontWeight.SemiBold : FontWeight.Normal;
        _viewedEye.IsVisible = ShowViewedIndicator;
        _name.FontWeight = NameFontWeight;
    }

    private void OnStageCheckClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: FileItemViewModel file })
            return;

        e.Handled = true;
        var command = StageToggleCommand;
        if (command is null &&
            TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel vm)
        {
            command = vm.WorkingCopy.ToggleFileStagedCommand;
        }

        if (command?.CanExecute(file) == true)
            command.Execute(file);
    }


    private static IBrush ThemeBrush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var res) == true && res is IBrush brush
            ? brush
            : Brushes.Gray;

    private static FontFamily ThemeFontFamily(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var res) == true && res is FontFamily ff
            ? ff
            : FontFamily.Default;

    private static double ThemeDouble(string key, double fallback) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var res) == true && res is double d
            ? d
            : fallback;

    private static MaterialIcon Icon(MaterialIconKind kind, double size, double opacity = 1) =>
        new()
        {
            Kind = kind,
            Width = size,
            Height = size,
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static MaterialIconKind StatusIcon(ChangeKind kind) => kind switch
    {
        ChangeKind.Added or ChangeKind.Untracked => MaterialIconKind.Plus,
        ChangeKind.Deleted => MaterialIconKind.Minus,
        ChangeKind.Modified => MaterialIconKind.Pencil,
        ChangeKind.Renamed => MaterialIconKind.FileReplaceOutline,
        ChangeKind.Copied => MaterialIconKind.ContentCopy,
        ChangeKind.TypeChanged => MaterialIconKind.FileCogOutline,
        ChangeKind.Conflicted => MaterialIconKind.AlertOctagonOutline,
        ChangeKind.Ignored => MaterialIconKind.EyeOffOutline,
        _ => MaterialIconKind.FileDocumentOutline,
    };

    private static string StatusTooltip(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "Added",
        ChangeKind.Untracked => "Untracked",
        ChangeKind.Deleted => "Deleted",
        ChangeKind.Modified => "Modified",
        ChangeKind.Renamed => "Renamed",
        ChangeKind.Copied => "Copied",
        ChangeKind.TypeChanged => "Type changed",
        ChangeKind.Conflicted => "Conflicted",
        ChangeKind.Ignored => "Ignored",
        _ => kind.ToString(),
    };
}

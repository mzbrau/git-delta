using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using Material.Icons;

namespace CodeReviewr.App.Converters;

public static class ForgeConverters
{
    public static readonly IValueConverter InvertBool =
        new FuncValueConverter<bool, bool>(v => !v);

    public static readonly IValueConverter IsNotNull =
        new FuncValueConverter<object?, bool>(v => v is not null);

    public static readonly IValueConverter IsNull =
        new FuncValueConverter<object?, bool>(v => v is null);

    public static readonly IValueConverter IsNotNullOrEmpty =
        new FuncValueConverter<string?, bool>(v => !string.IsNullOrEmpty(v));

    public static readonly IValueConverter PlusPrefix =
        new FuncValueConverter<int, string>(v => $"+{v}");

    public static readonly IValueConverter MinusPrefix =
        new FuncValueConverter<int, string>(v => $"-{v}");

    public static readonly IValueConverter StatusBadgeBrush =
        new FuncValueConverter<ChangeKind, IBrush>(kind => kind switch
        {
            ChangeKind.Added or ChangeKind.Copied => Brush("ForgeStatusAddedBrush"),
            ChangeKind.Deleted => Brush("ForgeStatusDeletedBrush"),
            ChangeKind.Modified or ChangeKind.Renamed or ChangeKind.TypeChanged => Brush("ForgeStatusModifiedBrush"),
            ChangeKind.Conflicted => Brush("ForgeErrorBrush"),
            _ => Brush("ForgeStatusUntrackedBrush"),
        });

    public static readonly IValueConverter StatusBadgeBackground =
        new FuncValueConverter<ChangeKind, IBrush>(kind => kind switch
        {
            ChangeKind.Added or ChangeKind.Copied => Brush("ForgeStatusAddedBadgeBgBrush"),
            ChangeKind.Deleted => Brush("ForgeStatusDeletedBadgeBgBrush"),
            ChangeKind.Modified or ChangeKind.Renamed or ChangeKind.TypeChanged => Brush("ForgeStatusModifiedBadgeBgBrush"),
            _ => Brush("ForgeStatusUntrackedBadgeBgBrush"),
        });

    public static readonly IValueConverter ChevronKind =
        new FuncValueConverter<bool, MaterialIconKind>(expanded =>
            expanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight);

    public static readonly IValueConverter IsPositive =
        new FuncValueConverter<int, bool>(v => v > 0);

    public static readonly IValueConverter SelectedFontWeight =
        new FuncValueConverter<bool, FontWeight>(selected =>
            selected ? FontWeight.SemiBold : FontWeight.Normal);

    public static readonly IValueConverter IsSideBySide =
        new FuncValueConverter<DiffViewMode, bool>(mode => mode == DiffViewMode.SideBySide);

    public static readonly IValueConverter CommitDateDisplay =
        new FuncValueConverter<DateTimeOffset, string>(WorkingCopyViewModel.FormatCommitDate);

    public static readonly IValueConverter GitConsoleLineBrush =
        new FuncValueConverter<GitConsoleLineKind, IBrush>(kind => kind switch
        {
            GitConsoleLineKind.Command => Brush("ForgePrimaryBrush"),
            GitConsoleLineKind.Stderr => Brush("ForgeErrorBrush"),
            GitConsoleLineKind.Meta => Brush("ForgeOnSurfaceVariantBrush"),
            _ => Brush("ForgeOnSurfaceBrush"),
        });

    private static IBrush Brush(string key)
    {
        if (Avalonia.Application.Current?.TryGetResource(key, Avalonia.Application.Current.ActualThemeVariant, out var res) == true
            && res is IBrush brush)
            return brush;
        return Brushes.Gray;
    }
}

public sealed class FuncValueConverter<TIn, TOut>(Func<TIn?, TOut> convert) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        convert(value is TIn t ? t : default);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

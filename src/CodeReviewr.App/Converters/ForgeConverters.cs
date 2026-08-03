using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Core.AI;
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

    public static readonly IValueConverter IsNullOrEmpty =
        new FuncValueConverter<string?, bool>(v => string.IsNullOrEmpty(v));

    public static readonly IValueConverter NullOrEmptyDisplay =
        new FuncValueConverter<string?, string>(v => string.IsNullOrEmpty(v) ? "—" : v!);

    public static readonly IValueConverter CheckStateBadgeBrush =
        new FuncValueConverter<string?, IBrush>(state => CheckStateCategory(state) switch
        {
            CheckCategory.Success => Brush("ForgeStatusAddedBrush"),
            CheckCategory.Failure => Brush("ForgeStatusDeletedBrush"),
            CheckCategory.Pending => Brush("ForgeStatusModifiedBrush"),
            _ => Brush("ForgeOnSurfaceVariantBrush"),
        });

    public static readonly IValueConverter CheckStateBadgeBackground =
        new FuncValueConverter<string?, IBrush>(state => CheckStateCategory(state) switch
        {
            CheckCategory.Success => Brush("ForgeStatusAddedBadgeBgBrush"),
            CheckCategory.Failure => Brush("ForgeStatusDeletedBadgeBgBrush"),
            CheckCategory.Pending => Brush("ForgeStatusModifiedBadgeBgBrush"),
            _ => Brush("ForgeStatusUntrackedBadgeBgBrush"),
        });

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

    public static readonly IValueConverter RelativeTime =
        new FuncValueConverter<DateTimeOffset?, string>(date =>
            date is null ? string.Empty : FormatRelativeTime(date.Value));

    public static string FormatRelativeTime(DateTimeOffset date)
    {
        if (date == default || date == DateTimeOffset.MinValue)
            return string.Empty;

        var elapsed = DateTimeOffset.Now - date.ToLocalTime();
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed.TotalSeconds < 60)
            return "just now";
        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24)
            return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 14)
            return $"{(int)elapsed.TotalDays}d ago";

        var local = date.ToLocalTime();
        return local.Year == DateTimeOffset.Now.Year
            ? local.ToString("d MMM")
            : local.ToString("d MMM yyyy");
    }

    public static readonly IValueConverter AiStarsDisplay =
        new FuncValueConverter<int, string>(v => new string('★', Math.Clamp(v, 0, 5)));

    public static readonly IValueConverter AiRiskBadgeBackground =
        new FuncValueConverter<AiRiskLevel, IBrush>(risk => risk switch
        {
            AiRiskLevel.Low => Brush("ForgeStatusAddedBadgeBgBrush"),
            AiRiskLevel.Medium => Brush("ForgeStatusModifiedBadgeBgBrush"),
            AiRiskLevel.High or AiRiskLevel.Critical => Brush("ForgeStatusDeletedBadgeBgBrush"),
            _ => Brush("ForgeStatusUntrackedBadgeBgBrush"),
        });

    public static readonly IValueConverter AiRiskBadgeBrush =
        new FuncValueConverter<AiRiskLevel, IBrush>(risk => risk switch
        {
            AiRiskLevel.Low => Brush("ForgeStatusAddedBrush"),
            AiRiskLevel.Medium => Brush("ForgeStatusModifiedBrush"),
            AiRiskLevel.High or AiRiskLevel.Critical => Brush("ForgeStatusDeletedBrush"),
            _ => Brush("ForgeOnSurfaceVariantBrush"),
        });

    public static readonly IValueConverter AiAnnotationSeverityBrush =
        new FuncValueConverter<AiAnnotationSeverity, IBrush>(severity => severity switch
        {
            AiAnnotationSeverity.Risk => Brush("ForgeStatusDeletedBrush"),
            AiAnnotationSeverity.Warning => Brush("ForgeStatusModifiedBrush"),
            AiAnnotationSeverity.Suggestion => Brush("ForgeAiAccentBrush"),
            _ => Brush("ForgeOnSurfaceVariantBrush"),
        });

    public static readonly IValueConverter GitConsoleLineBrush =
        new FuncValueConverter<GitConsoleLineKind, IBrush>(kind => kind switch
        {
            GitConsoleLineKind.Command => Brush("ForgePrimaryBrush"),
            GitConsoleLineKind.Stderr => Brush("ForgeErrorBrush"),
            GitConsoleLineKind.Meta => Brush("ForgeOnSurfaceVariantBrush"),
            _ => Brush("ForgeOnSurfaceBrush"),
        });

    private enum CheckCategory { Neutral, Success, Failure, Pending }

    private static CheckCategory CheckStateCategory(string? state)
    {
        if (string.IsNullOrEmpty(state))
            return CheckCategory.Neutral;

        return state.ToUpperInvariant() switch
        {
            "SUCCESS" or "COMPLETED" or "APPROVED" or "PASS" or "PASSED" => CheckCategory.Success,
            "FAILURE" or "FAILED" or "ERROR" or "CANCELLED" or "TIMED_OUT" or "STARTUP_FAILURE"
                or "ACTION_REQUIRED" or "CHANGES_REQUESTED" or "REJECTED" => CheckCategory.Failure,
            "PENDING" or "QUEUED" or "IN_PROGRESS" or "WAITING" or "REQUESTED"
                or "EXPECTED" or "REVIEW_REQUIRED" or "UNSTABLE" => CheckCategory.Pending,
            _ => CheckCategory.Neutral,
        };
    }

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

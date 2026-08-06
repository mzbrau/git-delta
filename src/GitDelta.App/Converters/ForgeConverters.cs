using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.AI;
using Material.Icons;

namespace GitDelta.App.Converters;

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

    /// <summary>Scales a double by 0.8 (e.g. dialog size as 80% of window bounds).</summary>
    public static readonly IValueConverter Scale0_8 =
        new FuncValueConverter<double, double>(v => v * 0.8);

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
        new FuncValueConverter<object?, string>(v => $"+{AsInt(v)}");

    public static readonly IValueConverter MinusPrefix =
        new FuncValueConverter<object?, string>(v => $"-{AsInt(v)}");

    private static int AsInt(object? v) => v switch
    {
        int i => i,
        long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
        _ => 0,
    };

    public static readonly IValueConverter StatusBadgeBrush =
        new FuncValueConverter<ChangeKind, IBrush>(kind => kind switch
        {
            ChangeKind.Added or ChangeKind.Untracked or ChangeKind.Copied => Brush("ForgeStatusAddedBrush"),
            ChangeKind.Deleted => Brush("ForgeStatusDeletedBrush"),
            ChangeKind.Modified or ChangeKind.Renamed or ChangeKind.TypeChanged => Brush("ForgeStatusModifiedBrush"),
            ChangeKind.Conflicted => Brush("ForgeErrorBrush"),
            _ => Brush("ForgeStatusUntrackedBrush"),
        });

    public static readonly IValueConverter StatusBadgeBackground =
        new FuncValueConverter<ChangeKind, IBrush>(kind => kind switch
        {
            ChangeKind.Added or ChangeKind.Untracked or ChangeKind.Copied => Brush("ForgeStatusAddedBadgeBgBrush"),
            ChangeKind.Deleted => Brush("ForgeStatusDeletedBadgeBgBrush"),
            ChangeKind.Modified or ChangeKind.Renamed or ChangeKind.TypeChanged => Brush("ForgeStatusModifiedBadgeBgBrush"),
            _ => Brush("ForgeStatusUntrackedBadgeBgBrush"),
        });

    public static readonly IValueConverter StatusIconKind =
        new FuncValueConverter<ChangeKind, MaterialIconKind>(kind => kind switch
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
        });

    public static readonly IValueConverter StatusIconTooltip =
        new FuncValueConverter<ChangeKind, string>(kind => kind switch
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
        });

    public static readonly IValueConverter AiChangeClassificationIcon =
        new FuncValueConverter<AiChangeClassification?, MaterialIconKind>(kind => kind switch
        {
            AiChangeClassification.BehaviorChanged => MaterialIconKind.SwapHorizontal,
            AiChangeClassification.NewFeature => MaterialIconKind.StarOutline,
            AiChangeClassification.BugFix => MaterialIconKind.BugOutline,
            AiChangeClassification.RefactorOnly => MaterialIconKind.AutoFix,
            AiChangeClassification.Configuration => MaterialIconKind.CogOutline,
            AiChangeClassification.Tests => MaterialIconKind.FlaskOutline,
            AiChangeClassification.Documentation => MaterialIconKind.FileDocumentOutline,
            AiChangeClassification.DependencyUpdate => MaterialIconKind.PackageVariant,
            AiChangeClassification.BuildOrCi => MaterialIconKind.HammerWrench,
            AiChangeClassification.Deletion => MaterialIconKind.DeleteOutline,
            AiChangeClassification.Performance => MaterialIconKind.Speedometer,
            AiChangeClassification.Security => MaterialIconKind.ShieldOutline,
            AiChangeClassification.UiOrStyling => MaterialIconKind.PaletteOutline,
            AiChangeClassification.Generated => MaterialIconKind.RobotOutline,
            _ => MaterialIconKind.HelpCircleOutline,
        });

    public static readonly IValueConverter AiChangeClassificationTooltip =
        new FuncValueConverter<AiChangeClassification?, string?>(kind => kind switch
        {
            AiChangeClassification.BehaviorChanged => "Behavior changed",
            AiChangeClassification.NewFeature => "New feature",
            AiChangeClassification.BugFix => "Bug fix",
            AiChangeClassification.RefactorOnly => "Refactor only",
            AiChangeClassification.Configuration => "Configuration",
            AiChangeClassification.Tests => "Tests",
            AiChangeClassification.Documentation => "Documentation",
            AiChangeClassification.DependencyUpdate => "Dependency update",
            AiChangeClassification.BuildOrCi => "Build / CI",
            AiChangeClassification.Deletion => "Deletion",
            AiChangeClassification.Performance => "Performance",
            AiChangeClassification.Security => "Security",
            AiChangeClassification.UiOrStyling => "UI / styling",
            AiChangeClassification.Generated => "Generated",
            _ => null,
        });

    public static readonly IValueConverter AiChangeClassificationLabel =
        new FuncValueConverter<AiChangeClassification?, string?>(kind =>
            (string?)AiChangeClassificationTooltip.Convert(kind, typeof(string), null, CultureInfo.InvariantCulture));

    public static readonly IValueConverter AiRiskBadgeBackground =
        new FuncValueConverter<AiRiskLevel?, IBrush>(risk => risk switch
        {
            AiRiskLevel.Low => new SolidColorBrush(Color.Parse("#2E7D32")),
            AiRiskLevel.Medium => new SolidColorBrush(Color.Parse("#EF6C00")),
            AiRiskLevel.High => new SolidColorBrush(Color.Parse("#C62828")),
            AiRiskLevel.Critical => new SolidColorBrush(Color.Parse("#6A1B9A")),
            _ => new SolidColorBrush(Color.Parse("#546E7A")),
        });

    public static readonly IValueConverter AiRiskLabel =
        new FuncValueConverter<AiRiskLevel?, string>(risk => risk?.ToString().ToUpperInvariant() ?? "");

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

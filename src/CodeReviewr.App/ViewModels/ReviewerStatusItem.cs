using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeReviewr.App.ViewModels;

public sealed partial class ReviewerStatusItem : ObservableObject
{
    private static readonly HttpClient Http = new();

    public ReviewerStatusItem(string login, string? avatarUrl, string state)
    {
        Login = login;
        AvatarUrl = avatarUrl;
        State = state;
        Initials = ComputeInitials(login);
    }

    public string Login { get; }
    public string? AvatarUrl { get; }
    public string State { get; }
    public string Initials { get; }

    public string Tooltip => $"{Login} · {FormatState(State)}";

    public bool ShowApprovedBadge =>
        string.Equals(State, "APPROVED", StringComparison.OrdinalIgnoreCase);

    public bool ShowChangesRequestedBadge =>
        string.Equals(State, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase);

    public bool HasStatusBadge => ShowApprovedBadge || ShowChangesRequestedBadge;

    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private bool _hasAvatar;

    public async Task LoadAvatarAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(AvatarUrl))
            return;

        try
        {
            using var response = await Http.GetAsync(AvatarUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            ms.Position = 0;
            var bitmap = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Avatar = bitmap;
                HasAvatar = true;
            });
        }
        catch
        {
            // Keep initials fallback.
        }
    }

    private static string ComputeInitials(string login)
    {
        if (string.IsNullOrWhiteSpace(login))
            return "?";

        var parts = login.Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";

        return login.Length >= 2
            ? login[..2].ToUpperInvariant()
            : login.ToUpperInvariant();
    }

    private static string FormatState(string state) => state.ToUpperInvariant() switch
    {
        "APPROVED" => "Approved",
        "CHANGES_REQUESTED" => "Requested changes",
        "COMMENTED" => "Commented",
        "REQUESTED" => "Review requested",
        _ => state,
    };
}

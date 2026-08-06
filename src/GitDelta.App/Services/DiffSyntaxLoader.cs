using Avalonia;
using Avalonia.Threading;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using GitDelta.Diff;

namespace GitDelta.App.Services;

/// <summary>
/// Loads left/right <see cref="FileSyntaxTokens"/> for a <see cref="FileDiff"/> off the UI thread,
/// then marshals assignment back via <paramref name="assignOnUi"/>.
/// Extracted from WorkingCopyViewModel to keep syntax concerns testable and reusable (Review VM).
/// </summary>
public sealed class DiffSyntaxLoader(
    IGitObjectReader objects,
    ISyntaxTokenService? syntaxTokens)
{
    public async Task LoadAsync(
        string repositoryPath,
        FilePath path,
        FileDiff diff,
        Func<ContentId, bool, CancellationToken, Task<string?>>? readWorktreeSide,
        Action<FileSyntaxTokens?, FileSyntaxTokens?> assignOnUi,
        CancellationToken ct)
    {
        if (syntaxTokens is null)
        {
            await InvokeOnUiAsync(() => assignOnUi(null, null)).ConfigureAwait(false);
            return;
        }

        try
        {
            string? leftText;
            string? rightText;

            if (readWorktreeSide is not null)
            {
                leftText = await readWorktreeSide(diff.OldContent, false, ct).ConfigureAwait(false);
                rightText = await readWorktreeSide(diff.NewContent, true, ct).ConfigureAwait(false);
            }
            else
            {
                leftText = await ReadBlobTextAsync(repositoryPath, diff.OldContent, ct).ConfigureAwait(false);
                rightText = await ReadBlobTextAsync(repositoryPath, diff.NewContent, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            FileSyntaxTokens? left = null;
            FileSyntaxTokens? right = null;
            if (leftText is not null)
            {
                left = await syntaxTokens.TokeniseAsync(diff.OldContent, path, leftText, ct)
                    .ConfigureAwait(false);
            }

            if (rightText is not null)
            {
                right = await syntaxTokens.TokeniseAsync(diff.NewContent, path, rightText, ct)
                    .ConfigureAwait(false);
            }

            await InvokeOnUiAsync(() => assignOnUi(left, right)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await InvokeOnUiAsync(() => assignOnUi(null, null)).ConfigureAwait(false);
        }
    }

    private async Task<string?> ReadBlobTextAsync(string repositoryPath, ContentId content, CancellationToken ct)
    {
        if (content.IsEmpty) return null;
        var bytes = await objects.ReadBlobAsync(repositoryPath, content, ct).ConfigureAwait(false);
        return DecodeUtf8(bytes);
    }

    private static string? DecodeUtf8(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        var offset = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static async Task InvokeOnUiAsync(Action action)
    {
        try
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            if (Application.Current is null)
            {
                action();
                return;
            }

            await dispatcher.InvokeAsync(action);
        }
        catch (InvalidOperationException)
        {
            action();
        }
    }
}

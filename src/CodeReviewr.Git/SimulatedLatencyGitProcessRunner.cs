using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.Git;

/// <summary>
/// Temporary decorator that optionally delays every git subprocess by a random 1–5 seconds
/// when <see cref="CodeReviewr.Core.AppSettings.SimulateSlowGit"/> is enabled.
/// Intended for testing UI responsiveness under slow AV / disk environments; remove later.
/// </summary>
public sealed class SimulatedLatencyGitProcessRunner : IGitProcessRunner
{
    private readonly IGitProcessRunner _inner;
    private readonly ISettingsStore _settings;
    private readonly Func<int> _sampleDelayMs;

    public SimulatedLatencyGitProcessRunner(
        IGitProcessRunner inner,
        ISettingsStore settings,
        Func<int>? sampleDelayMs = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sampleDelayMs = sampleDelayMs ?? (() => Random.Shared.Next(1000, 5001));
    }

    public string ExecutablePath => _inner.ExecutablePath;

    public void SetExecutablePath(string executablePath) => _inner.SetExecutablePath(executablePath);

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitProcessOptions? options = null,
        CancellationToken ct = default)
    {
        await MaybeDelayAsync(ct).ConfigureAwait(false);
        return await _inner.RunAsync(workingDirectory, arguments, options, ct).ConfigureAwait(false);
    }

    public ILongLivedGitProcess StartLongLived(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default)
    {
        // StartLongLived is synchronous; block on the delay so startup still respects the toggle.
        // Callers already run this off the UI thread.
        MaybeDelayAsync(ct).GetAwaiter().GetResult();
        return _inner.StartLongLived(workingDirectory, arguments, ct);
    }

    private async Task MaybeDelayAsync(CancellationToken ct)
    {
        if (!_settings.Current.SimulateSlowGit)
            return;

        var ms = _sampleDelayMs();
        if (ms > 0)
            await Task.Delay(ms, ct).ConfigureAwait(false);
    }
}

using System.Diagnostics;
using GitDelta.Core.Abstractions;

namespace GitDelta.Persistence;

/// <summary>macOS Keychain-backed token store using the <c>security</c> CLI.</summary>
public sealed class KeychainTokenStore : ITokenStore
{
    private const string ServiceName = "GitDelta";

    /// <summary>macOS <c>errSecItemNotFound</c> — item missing from the keychain.</summary>
    public const int ErrSecItemNotFound = 44;

    public async Task SetTokenAsync(string host, string login, string token, CancellationToken ct = default)
    {
        if (token.Contains('\r', StringComparison.Ordinal) || token.Contains('\n', StringComparison.Ordinal))
            throw new ArgumentException("Token must not contain CR or LF characters.", nameof(token));

        // Write via `security -i` stdin so the token never appears on process argv / `ps`.
        var account = MemoryTokenStore.MakeKey(host, login);
        // Escape for security interactive command language: backslash and quotes.
        var escaped = token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var script =
            $"add-generic-password -a \"{account}\" -s \"{ServiceName}\" -w \"{escaped}\" -U\n";
        await RunSecurityInteractiveAsync(script, ct, allowNotFound: false).ConfigureAwait(false);
    }

    public async Task<string?> GetTokenAsync(string host, string login, CancellationToken ct = default)
    {
        var account = MemoryTokenStore.MakeKey(host, login);
        return await RunSecurityAsync(
            ct,
            allowNotFound: true,
            "find-generic-password",
            "-a", account,
            "-s", ServiceName,
            "-w").ConfigureAwait(false);
    }

    public async Task DeleteTokenAsync(string host, string login, CancellationToken ct = default)
    {
        var account = MemoryTokenStore.MakeKey(host, login);
        await RunSecurityAsync(
            ct,
            allowNotFound: true,
            "delete-generic-password",
            "-a", account,
            "-s", ServiceName).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a <c>security</c> CLI exit to either success, not-found, or failure.
    /// Exposed for unit tests so we do not need a live keychain.
    /// </summary>
    public static string? InterpretSecurityResult(int exitCode, string stdout, string stderr, bool allowNotFound)
    {
        if (exitCode == 0)
            return stdout.TrimEnd('\n', '\r');

        if (allowNotFound && IsNotFound(exitCode, stderr))
            return null;

        var message = stderr.Trim().Length > 0 ? stderr.Trim() : $"security exited with code {exitCode}.";
        throw new InvalidOperationException(message);
    }

    public static bool IsNotFound(int exitCode, string stderr) =>
        exitCode == ErrSecItemNotFound ||
        stderr.Contains("could not be found", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> RunSecurityInteractiveAsync(
        string script,
        CancellationToken ct,
        bool allowNotFound)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "security",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-i");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start security process.");

        await process.StandardInput.WriteAsync(script.AsMemory(), ct).ConfigureAwait(false);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return InterpretSecurityResult(process.ExitCode, stdout, stderr, allowNotFound);
    }

    private static async Task<string?> RunSecurityAsync(
        CancellationToken ct,
        bool allowNotFound,
        params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "security",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start security process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return InterpretSecurityResult(process.ExitCode, stdout, stderr, allowNotFound);
    }
}

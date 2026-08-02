using System.Diagnostics;
using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.Persistence;

/// <summary>macOS Keychain-backed token store using the <c>security</c> CLI.</summary>
public sealed class KeychainTokenStore : ITokenStore
{
    private const string ServiceName = "CodeReviewr";

    public async Task SetTokenAsync(string host, string login, string token, CancellationToken ct = default)
    {
        await DeleteTokenAsync(host, login, ct).ConfigureAwait(false);

        var account = MemoryTokenStore.MakeKey(host, login);
        await RunSecurityAsync(
            ct,
            "add-generic-password",
            "-a", account,
            "-s", ServiceName,
            "-w", token,
            "-U").ConfigureAwait(false);
    }

    public async Task<string?> GetTokenAsync(string host, string login, CancellationToken ct = default)
    {
        var account = MemoryTokenStore.MakeKey(host, login);
        try
        {
            return await RunSecurityAsync(
                ct,
                "find-generic-password",
                "-a", account,
                "-s", ServiceName,
                "-w").ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("could not be found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    public Task DeleteTokenAsync(string host, string login, CancellationToken ct = default)
    {
        var account = MemoryTokenStore.MakeKey(host, login);
        return RunSecurityAsync(
            ct,
            "delete-generic-password",
            "-a", account,
            "-s", ServiceName);
    }

    private static async Task<string> RunSecurityAsync(CancellationToken ct, params string[] args)
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

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(stderr.Trim().Length > 0 ? stderr.Trim() : $"security exited with code {process.ExitCode}.");

        return stdout.TrimEnd('\n', '\r');
    }
}

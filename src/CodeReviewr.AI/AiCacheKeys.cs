using System.Security.Cryptography;
using System.Text;

namespace CodeReviewr.AI;

/// <summary>
/// Deterministic cache keys for AI results, so re-running the same review under the same rules,
/// prompt version, and model reuses a cached result instead of re-invoking Copilot.
/// </summary>
public static class AiCacheKeys
{
    public static string ComputePrTriageKey(
        string sessionKey,
        string headSha,
        string mergeBaseSha,
        string scope,
        string promptVersion,
        string? model,
        string rulesHash,
        string instructionsHash) =>
        Hash(string.Join(
            '|',
            "pr-triage",
            sessionKey,
            headSha,
            mergeBaseSha,
            scope,
            promptVersion,
            model ?? "",
            rulesHash,
            instructionsHash));

    public static string ComputeFileKey(
        string path,
        string? beforeOid,
        string? afterOid,
        string promptVersion,
        string? model,
        string rulesHash,
        string instructionsHash) =>
        Hash(string.Join(
            '|',
            "file",
            path,
            beforeOid ?? "",
            afterOid ?? "",
            promptVersion,
            model ?? "",
            rulesHash,
            instructionsHash));

    /// <summary>Short (16 hex char) SHA-256 digest, used both as a standalone hash and as a cache key component.</summary>
    public static string Hash(string? value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}

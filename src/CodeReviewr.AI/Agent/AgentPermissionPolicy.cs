namespace CodeReviewr.AI.Agent;

/// <summary>
/// Deny-by-default permission policy for agent tool calls. Only read-style tool kinds
/// (<c>read</c>, <c>glob</c>, <c>grep</c>, <c>view</c>) are allowed, and only when the target
/// path does not match a secret/credential pattern. Everything else — shell, write/edit, custom
/// tools that were not registered with <c>SkipPermission</c>, etc. — is denied.
/// </summary>
internal sealed class AgentPermissionPolicy
{
    private static readonly string[] BuiltInPathDenylist =
    [
        ".env",
        ".env.*",
        "*.pem",
        "*.key",
        "*.p12",
        "*.pfx",
        "id_rsa",
        "id_ed25519",
        "credentials.json",
        "secrets.json",
    ];

    private static readonly HashSet<string> AllowedReadKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "read",
        "glob",
        "grep",
        "view",
    };

    private readonly List<string> _pathDenylist;
    private readonly List<AgentPermissionRequest> _denials = [];
    private readonly Lock _lock = new();

    public AgentPermissionPolicy(IReadOnlyList<string>? userPathDenylist = null)
    {
        _pathDenylist = [.. BuiltInPathDenylist, .. userPathDenylist ?? []];
    }

    /// <summary>Denied requests recorded so far, for surfacing to the user (e.g. a diagnostics panel).</summary>
    public IReadOnlyList<AgentPermissionRequest> Denials
    {
        get
        {
            lock (_lock)
                return [.. _denials];
        }
    }

    public AgentPermissionDecision Evaluate(AgentPermissionRequest request)
    {
        var decision = EvaluateCore(request);
        if (decision == AgentPermissionDecision.Deny)
        {
            lock (_lock)
                _denials.Add(request);
        }

        return decision;
    }

    private AgentPermissionDecision EvaluateCore(AgentPermissionRequest request)
    {
        if (!AllowedReadKinds.Contains(request.Kind))
            return AgentPermissionDecision.Deny;

        if (!string.IsNullOrEmpty(request.Path) && IsDeniedPath(request.Path))
            return AgentPermissionDecision.Deny;

        return AgentPermissionDecision.Approve;
    }

    private bool IsDeniedPath(string path)
    {
        var normalised = path.Replace('\\', '/');
        var fileName = Path.GetFileName(normalised);

        foreach (var pattern in _pathDenylist)
        {
            if (MatchesGlob(fileName, pattern) || MatchesGlob(normalised, pattern))
                return true;
        }

        return false;
    }

    private static bool MatchesGlob(string value, string pattern)
    {
        if (pattern.Length == 0)
            return false;

        if (pattern.Length > 1 && pattern[0] == '*' && pattern[^1] == '*')
            return value.Contains(pattern[1..^1], StringComparison.OrdinalIgnoreCase);

        if (pattern[0] == '*')
            return value.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);

        if (pattern[^1] == '*')
            return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);

        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }
}

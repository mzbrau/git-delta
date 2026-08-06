namespace GitDelta.Core;

/// <summary>GitHub account metadata persisted in settings. Tokens are stored separately via <see cref="Abstractions.ITokenStore"/>.</summary>
public sealed record GitHubAccountSettings
{
    public required string Host { get; init; }
    public required string Login { get; init; }
    public string? AvatarUrl { get; init; }
    public bool NeedsReauth { get; init; }
}

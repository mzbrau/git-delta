namespace GitDelta.Core;

/// <summary>Maps a local repository clone to a GitHub account and remote identity.</summary>
public sealed record RepositoryAccountBinding
{
    public string? GitHubNodeId { get; init; }
    public required string Host { get; init; }
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public required string LocalPath { get; init; }
    public required string AccountLogin { get; init; }
}

namespace GitDelta.Review;

/// <summary>Thrown when a pull request cannot be opened because no matching local clone exists.</summary>
public sealed class LocalCloneRequiredException : Exception
{
    public LocalCloneRequiredException(string host, string owner, string name, string cloneUrl, string suggestedPath)
        : base($"No local clone found for {owner}/{name} on {host}.")
    {
        Host = host;
        Owner = owner;
        Name = name;
        CloneUrl = cloneUrl;
        SuggestedPath = suggestedPath;
    }

    public string Host { get; }
    public string Owner { get; }
    public string Name { get; }
    public string CloneUrl { get; }
    public string SuggestedPath { get; }
}

namespace GitDelta.Core;

/// <summary>Git commit object name (SHA-1/SHA-256).</summary>
public readonly record struct CommitId(string Value)
{
    public static CommitId FromSha(string sha) => new(sha);
    public override string ToString() => Value;
}

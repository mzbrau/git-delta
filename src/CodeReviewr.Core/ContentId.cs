namespace CodeReviewr.Core;

/// <summary>
/// Content identity: blob SHA for committed/index content, or a content hash for worktree content.
/// Basis of every content-addressed cache key.
/// </summary>
public readonly record struct ContentId(string Value)
{
    public static ContentId Empty { get; } = new("0".PadRight(40, '0'));
    public static ContentId FromSha(string sha) => new(sha);
    public static ContentId FromBytes(ReadOnlySpan<byte> bytes)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return new(Convert.ToHexStringLower(hash));
    }

    public bool IsEmpty => Value == Empty.Value || string.IsNullOrEmpty(Value);
    public override string ToString() => Value;
}

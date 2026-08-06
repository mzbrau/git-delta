namespace GitDelta.Core;

/// <summary>Repository-relative path with forward slashes.</summary>
public readonly record struct FilePath(string Value) : IComparable<FilePath>
{
    public static FilePath From(string path) =>
        new(path.Replace('\\', '/').TrimStart('/'));

    public string Name => Path.GetFileName(Value);
    public string? Directory => Path.GetDirectoryName(Value)?.Replace('\\', '/');

    public int CompareTo(FilePath other) =>
        StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value;
}

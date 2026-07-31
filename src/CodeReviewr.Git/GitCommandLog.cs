namespace CodeReviewr.Git;

public sealed class GitCommandLog : IGitCommandLog
{
    public const int MaxEntries = 200;
    public const int MaxStreamChars = 16_384;

    private readonly object _gate = new();
    private readonly List<GitCommandLogEntry> _entries = [];

    public event EventHandler? Changed;

    public IReadOnlyList<GitCommandLogEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToList();
        }
    }

    public void Append(GitCommandLogEntry entry)
    {
        var trimmed = entry with
        {
            Stdout = Truncate(entry.Stdout),
            Stderr = Truncate(entry.Stderr),
        };

        lock (_gate)
        {
            _entries.Add(trimmed);
            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxStreamChars)
            return value;
        return value[..MaxStreamChars] + "\n… (truncated)";
    }
}

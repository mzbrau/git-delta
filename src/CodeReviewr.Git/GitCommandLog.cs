namespace CodeReviewr.Git;

public sealed class GitCommandLog : IGitCommandLog
{
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
        lock (_gate)
            _entries.Add(entry);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}


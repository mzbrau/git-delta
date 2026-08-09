using System.Collections.ObjectModel;
using GitDelta.Core;

namespace GitDelta.App.ViewModels;

/// <summary>Session-only MRU of files opened outside the change lists (max 5).</summary>
public sealed class RecentViewedFilesStore
{
    public const int MaxEntries = 5;

    private readonly ObservableCollection<FileItemViewModel> _items = [];
    private readonly object _lock = new();

    public ObservableCollection<FileItemViewModel> Items => _items;

    public bool HasItems => _items.Count > 0;

    public void Clear()
    {
        lock (_lock)
            _items.Clear();
    }

    /// <summary>
    /// Records <paramref name="path"/> at the front of the MRU. Skips when the path is present in
    /// <paramref name="excludePaths"/>. Returns the selected row VM.
    /// </summary>
    public FileItemViewModel Remember(FilePath path, IReadOnlySet<string> excludePaths)
    {
        lock (_lock)
        {
            if (excludePaths.Contains(path.Value))
            {
                RemovePathUnlocked(path.Value);
                return new FileItemViewModel(path, ChangeKind.Modified, isStagedList: false);
            }

            for (var i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].Path.Value, path.Value, StringComparison.Ordinal))
                {
                    var existing = _items[i];
                    if (i != 0)
                    {
                        _items.RemoveAt(i);
                        _items.Insert(0, existing);
                    }

                    return existing;
                }
            }

            var created = new FileItemViewModel(path, ChangeKind.Modified, isStagedList: false);
            _items.Insert(0, created);
            while (_items.Count > MaxEntries)
                _items.RemoveAt(_items.Count - 1);
            return created;
        }
    }

    /// <summary>Drops any recent entries that now appear in change lists.</summary>
    public void ExcludePaths(IReadOnlySet<string> excludePaths)
    {
        lock (_lock)
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                if (excludePaths.Contains(_items[i].Path.Value))
                    _items.RemoveAt(i);
            }
        }
    }

    public FileItemViewModel? Find(string path)
    {
        lock (_lock)
            return _items.FirstOrDefault(i => string.Equals(i.Path.Value, path, StringComparison.Ordinal));
    }

    private void RemovePathUnlocked(string path)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_items[i].Path.Value, path, StringComparison.Ordinal))
                _items.RemoveAt(i);
        }
    }
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GitDelta.App.Collections;

/// <summary>
/// <see cref="ObservableCollection{T}"/> that can replace its contents with a single
/// <see cref="NotifyCollectionChangedAction.Reset"/> notification — avoids N Add events
/// when rebuilding large lists (e.g. diff rows).
/// </summary>
public sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    public void Reset(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();

        Items.Clear();
        for (var i = 0; i < items.Count; i++)
            Items.Add(items[i]);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

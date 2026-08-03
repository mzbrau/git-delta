using System.Collections.Specialized;
using CodeReviewr.App.Collections;
using NUnit.Framework;

namespace CodeReviewr.App.Tests;

[TestFixture]
public sealed class ResettableObservableCollectionTests
{
    [Test]
    public void Reset_RaisesSingleResetNotification()
    {
        var collection = new ResettableObservableCollection<int> { 1, 2 };
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        collection.Reset([10, 20, 30]);

        Assert.That(actions, Is.EqualTo(new[] { NotifyCollectionChangedAction.Reset }));
        Assert.That(collection, Is.EqualTo(new[] { 10, 20, 30 }));
    }

    [Test]
    public void Reset_Empty_ClearsWithSingleReset()
    {
        var collection = new ResettableObservableCollection<string> { "a" };
        var count = 0;
        collection.CollectionChanged += (_, _) => count++;

        collection.Reset([]);

        Assert.That(count, Is.EqualTo(1));
        Assert.That(collection, Is.Empty);
    }
}

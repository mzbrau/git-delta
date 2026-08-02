using CodeReviewr.Persistence;
using NUnit.Framework;

namespace CodeReviewr.Persistence.Tests;

public sealed class SqliteStoreTests
{
    [Test]
    public void DisposableCacheStore_EnsureSchemaAndWipe_Works()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodeReviewr.Tests", Guid.NewGuid().ToString("N"), "cache.db");
        using var store = new SqliteDisposableCacheStore(path);

        store.EnsureSchema();
        Assert.That(store.SchemaVersion, Is.EqualTo(SqliteDisposableCacheStore.CurrentSchemaVersion));

        store.Wipe();
        Assert.That(store.SchemaVersion, Is.EqualTo(SqliteDisposableCacheStore.CurrentSchemaVersion));
    }

    [Test]
    public void DurableUserStore_EnsureSchema_Works()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodeReviewr.Tests", Guid.NewGuid().ToString("N"), "durable.db");
        using var store = new SqliteDurableUserStore(path);

        store.EnsureSchema();
        Assert.That(store.SchemaVersion, Is.EqualTo(SqliteDurableUserStore.CurrentSchemaVersion));
    }
}

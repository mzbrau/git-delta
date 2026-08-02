using CodeReviewr.Persistence;
using NUnit.Framework;

namespace CodeReviewr.Persistence.Tests;

public sealed class OutboxStoreTests
{
    [Test]
    public async Task Enqueue_List_Delete_Works()
    {
        var path = CreateDbPath();
        using var store = new SqliteDurableUserStore(path);
        store.EnsureSchema();

        var entry = CreateEntry(OutboxKind.AddComment);
        await store.EnqueueAsync(entry);
        var pending = await store.ListAsync(OutboxState.Pending);
        Assert.That(pending, Has.Count.EqualTo(1));
        Assert.That(pending[0].Id, Is.EqualTo(entry.Id));

        await store.DeleteAsync(entry.Id);
        Assert.That(await store.ListAsync(OutboxState.Pending), Is.Empty);
    }

    [Test]
    public async Task RecoverInFlight_RequeuesPending()
    {
        var path = CreateDbPath();
        using var store = new SqliteDurableUserStore(path);
        store.EnsureSchema();

        var entry = CreateEntry(OutboxKind.MarkFileViewed);
        await store.EnqueueAsync(entry);
        await store.MarkInFlightAsync(entry.Id);

        var inFlight = await store.ListAsync(OutboxState.InFlight);
        Assert.That(inFlight, Has.Count.EqualTo(1));

        await store.RecoverInFlightAsync();
        Assert.That(await store.ListAsync(OutboxState.InFlight), Is.Empty);
        Assert.That(await store.ListAsync(OutboxState.Pending), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task OfflineComment_SurvivesProcessRestart()
    {
        var path = CreateDbPath();
        var entry = CreateEntry(OutboxKind.AddComment);

        using (var store = new SqliteDurableUserStore(path))
        {
            store.EnsureSchema();
            await store.EnqueueAsync(entry);
        }

        using (var store = new SqliteDurableUserStore(path))
        {
            store.EnsureSchema();
            var pending = await store.ListAsync(OutboxState.Pending);
            Assert.That(pending, Has.Count.EqualTo(1));
            Assert.That(pending[0].Kind, Is.EqualTo(OutboxKind.AddComment));
            Assert.That(pending[0].PayloadJson, Does.Contain("hello"));
        }
    }

    [Test]
    public async Task LocalNotes_RoundTrip()
    {
        var path = CreateDbPath();
        using var store = new SqliteDurableUserStore(path);
        store.EnsureSchema();

        await store.SetNoteAsync("PR_1", "Need to verify tests.");
        var note = await store.GetNoteAsync("PR_1");
        Assert.That(note, Is.EqualTo("Need to verify tests."));
    }

    [Test]
    public async Task LocalViewed_RoundTrip()
    {
        var path = CreateDbPath();
        using var store = new SqliteDurableUserStore(path);
        store.EnsureSchema();

        await store.SetViewedAsync("PR_1", "src/a.cs", "abc123", DateTimeOffset.UtcNow);
        Assert.That(await store.IsViewedAsync("PR_1", "src/a.cs"), Is.True);
        await store.RemoveViewedAsync("PR_1", "src/a.cs");
        Assert.That(await store.IsViewedAsync("PR_1", "src/a.cs"), Is.False);
    }

    [Test]
    public void DurableUserStore_MigratesToSchemaV2()
    {
        var path = CreateDbPath();
        using var store = new SqliteDurableUserStore(path);
        store.EnsureSchema();
        Assert.That(store.SchemaVersion, Is.EqualTo(SqliteDurableUserStore.CurrentSchemaVersion));
    }

    private static string CreateDbPath() =>
        Path.Combine(Path.GetTempPath(), "CodeReviewr.Tests", Guid.NewGuid().ToString("N"), "durable.db");

    private static OutboxEntry CreateEntry(OutboxKind kind) =>
        new(
            Guid.NewGuid().ToString("N"),
            "github.com",
            "dev",
            "PR_NODE",
            kind,
            """{"owner":"acme","name":"demo","number":1,"data":{"body":"hello"}}""",
            DateTimeOffset.UtcNow,
            0,
            null,
            OutboxState.Pending);
}

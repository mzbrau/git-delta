using GitDelta.Core.AI;
using GitDelta.Core.Abstractions;
using GitDelta.Persistence;
using NUnit.Framework;

namespace GitDelta.Persistence.Tests;

public sealed class AiResultStoreTests
{
    [Test]
    public void EnsureSchema_ReachesVersion5()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        Assert.That(durable.SchemaVersion, Is.EqualTo(5));
    }

    [Test]
    public async Task UpsertRun_GetLatestAndById_RoundTrips()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteAiResultStore(path);

        var run = CreateRun("run-1", "PR_1");
        await store.UpsertRunAsync(run);

        var byId = await store.GetRunAsync("run-1");
        Assert.That(byId, Is.Not.Null);
        Assert.That(byId!.SessionKey, Is.EqualTo("PR_1"));
        Assert.That(byId.State, Is.EqualTo(AiRunState.Running));

        var latest = await store.GetLatestRunAsync("PR_1");
        Assert.That(latest, Is.Not.Null);
        Assert.That(latest!.Id, Is.EqualTo("run-1"));

        var updated = run with { State = AiRunState.Complete, TurnsUsed = 5, FinishedUtc = DateTimeOffset.UtcNow };
        await store.UpsertRunAsync(updated);

        var reread = await store.GetRunAsync("run-1");
        Assert.That(reread!.State, Is.EqualTo(AiRunState.Complete));
        Assert.That(reread.TurnsUsed, Is.EqualTo(5));
        Assert.That(reread.FinishedUtc, Is.Not.Null);
    }

    [Test]
    public async Task GetLatestRun_ReturnsMostRecentByStartedUtc()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteAiResultStore(path);

        var older = CreateRun("run-old", "PR_1") with { StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10) };
        var newer = CreateRun("run-new", "PR_1") with { StartedUtc = DateTimeOffset.UtcNow };
        await store.UpsertRunAsync(older);
        await store.UpsertRunAsync(newer);

        var latest = await store.GetLatestRunAsync("PR_1");
        Assert.That(latest!.Id, Is.EqualTo("run-new"));
    }

    [Test]
    public async Task UpsertPrResult_GetByCacheKeyAndRun_RoundTrips()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteAiResultStore(path);

        await store.UpsertRunAsync(CreateRun("run-1", "PR_1"));
        var result = new AiPrResultRecord("run-1", "PR_1", "cache-key-1", """{"summary":"ok"}""", DateTimeOffset.UtcNow);
        await store.UpsertPrResultAsync(result);

        var byCacheKey = await store.GetPrResultByCacheKeyAsync("cache-key-1");
        Assert.That(byCacheKey, Is.Not.Null);
        Assert.That(byCacheKey!.RunId, Is.EqualTo("run-1"));
        Assert.That(byCacheKey.PayloadJson, Does.Contain("ok"));

        var byRun = await store.GetPrResultForRunAsync("run-1");
        Assert.That(byRun, Is.Not.Null);
        Assert.That(byRun!.CacheKey, Is.EqualTo("cache-key-1"));

        var updated = result with { PayloadJson = """{"summary":"updated"}""" };
        await store.UpsertPrResultAsync(updated);
        var reread = await store.GetPrResultByCacheKeyAsync("cache-key-1");
        Assert.That(reread!.PayloadJson, Does.Contain("updated"));
    }

    [Test]
    public async Task UpsertPrResult_SameCacheKeyDifferentRun_UpdatesViaCacheKeyConflict()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteAiResultStore(path);

        await store.UpsertRunAsync(CreateRun("run-1", "PR_1"));
        await store.UpsertRunAsync(CreateRun("run-2", "PR_1"));
        await store.UpsertPrResultAsync(new AiPrResultRecord(
            "run-1", "PR_1", "shared-cache", """{"summary":"first"}""", DateTimeOffset.UtcNow));

        await store.UpsertPrResultAsync(new AiPrResultRecord(
            "run-2", "PR_1", "shared-cache", """{"summary":"second"}""", DateTimeOffset.UtcNow));

        var byCacheKey = await store.GetPrResultByCacheKeyAsync("shared-cache");
        Assert.That(byCacheKey, Is.Not.Null);
        Assert.That(byCacheKey!.RunId, Is.EqualTo("run-2"));
        Assert.That(byCacheKey.PayloadJson, Does.Contain("second"));
    }

    [Test]
    public async Task UpsertFileResult_GetByCacheKeyAndListForRun_RoundTrips()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteAiResultStore(path);

        await store.UpsertRunAsync(CreateRun("run-1", "PR_1"));
        var file1 = new AiFileResultRecord(
            "run-1", "PR_1", "src/a.cs", "file-cache-1", "BugFix", null, DateTimeOffset.UtcNow);
        var file2 = new AiFileResultRecord(
            "run-1", "PR_1", "src/b.cs", "file-cache-2", "RefactorOnly", """{"purpose":"x"}""", DateTimeOffset.UtcNow);
        await store.UpsertFileResultAsync(file1);
        await store.UpsertFileResultAsync(file2);

        var byCacheKey = await store.GetFileResultByCacheKeyAsync("file-cache-1");
        Assert.That(byCacheKey, Is.Not.Null);
        Assert.That(byCacheKey!.Path, Is.EqualTo("src/a.cs"));
        Assert.That(byCacheKey.Classification, Is.EqualTo("BugFix"));

        var forRun = await store.ListFileResultsForRunAsync("run-1");
        Assert.That(forRun, Has.Count.EqualTo(2));

        var updated = file1 with { Classification = "Skip" };
        await store.UpsertFileResultAsync(updated);
        var reread = await store.GetFileResultByCacheKeyAsync("file-cache-1");
        Assert.That(reread!.Classification, Is.EqualTo("Skip"));
        Assert.That(await store.ListFileResultsForRunAsync("run-1"), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Annotations_UpsertListAndReadState_Work()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteAiResultStore(path);

        await store.UpsertRunAsync(CreateRun("run-1", "PR_1"));
        var annotation = new AiAnnotationRecord(
            "ann-1", "run-1", "PR_1", "src/a.cs", "blob-oid", 10, 12, "New", "Warning",
            "Consider handling this edge case.", AiAnnotationReadState.Unread, DateTimeOffset.UtcNow);
        await store.UpsertAnnotationAsync(annotation);

        var listed = await store.ListAnnotationsAsync("PR_1");
        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0].Id, Is.EqualTo("ann-1"));
        Assert.That(listed[0].ReadState, Is.EqualTo(AiAnnotationReadState.Unread));

        await store.SetAnnotationReadStateAsync("ann-1", AiAnnotationReadState.Dismissed);
        var afterDismiss = await store.ListAnnotationsAsync("PR_1");
        Assert.That(afterDismiss, Is.Empty);

        var includingDismissed = await store.ListAnnotationsAsync("PR_1", includeDismissed: true);
        Assert.That(includingDismissed, Has.Count.EqualTo(1));
        Assert.That(includingDismissed[0].ReadState, Is.EqualTo(AiAnnotationReadState.Dismissed));

        var filteredByPath = await store.ListAnnotationsAsync("PR_1", path: "src/other.cs", includeDismissed: true);
        Assert.That(filteredByPath, Is.Empty);
    }

    [Test]
    public async Task ChatMessages_AppendAndList_RoundTrips()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteAiResultStore(path);

        await store.AppendChatMessageAsync("PR_1", new AiChatMessage("user", "Why was this changed?", DateTimeOffset.UtcNow));
        await store.AppendChatMessageAsync("PR_1", new AiChatMessage("assistant", "Because of a bug fix.", DateTimeOffset.UtcNow));

        var messages = await store.ListChatMessagesAsync("PR_1");
        Assert.That(messages, Has.Count.EqualTo(2));
        Assert.That(messages[0].Role, Is.EqualTo("user"));
        Assert.That(messages[1].Role, Is.EqualTo("assistant"));
    }

    [Test]
    public async Task ClearChatMessages_RemovesOnlyThatPr()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteAiResultStore(path);

        await store.AppendChatMessageAsync("PR_1", new AiChatMessage("user", "hi", DateTimeOffset.UtcNow));
        await store.AppendChatMessageAsync("PR_1", new AiChatMessage("assistant", "hello", DateTimeOffset.UtcNow));
        await store.AppendChatMessageAsync("PR_2", new AiChatMessage("user", "other", DateTimeOffset.UtcNow));

        await store.ClearChatMessagesAsync("PR_1");

        Assert.That(await store.ListChatMessagesAsync("PR_1"), Is.Empty);
        Assert.That(await store.ListChatMessagesAsync("PR_2"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ClearAll_RemovesAllAiData()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteAiResultStore(path);

        await store.UpsertRunAsync(CreateRun("run-1", "PR_1"));
        await store.UpsertPrResultAsync(new AiPrResultRecord("run-1", "PR_1", "cache-key-1", "{}", DateTimeOffset.UtcNow));
        await store.UpsertFileResultAsync(new AiFileResultRecord(
            "run-1", "PR_1", "src/a.cs", "file-cache-1", "RefactorOnly", null, DateTimeOffset.UtcNow));
        await store.UpsertAnnotationAsync(new AiAnnotationRecord(
            "ann-1", "run-1", "PR_1", "src/a.cs", "blob-oid", 1, 2, "New", "Info", "note",
            AiAnnotationReadState.Unread, DateTimeOffset.UtcNow));
        await store.AppendChatMessageAsync("PR_1", new AiChatMessage("user", "hi", DateTimeOffset.UtcNow));

        await store.ClearAllAsync();

        Assert.That(await store.GetRunAsync("run-1"), Is.Null);
        Assert.That(await store.GetPrResultByCacheKeyAsync("cache-key-1"), Is.Null);
        Assert.That(await store.GetFileResultByCacheKeyAsync("file-cache-1"), Is.Null);
        Assert.That(await store.ListAnnotationsAsync("PR_1", includeDismissed: true), Is.Empty);
        Assert.That(await store.ListChatMessagesAsync("PR_1"), Is.Empty);
    }

    private static string CreateDbPath() =>
        Path.Combine(Path.GetTempPath(), "GitDelta.Tests", Guid.NewGuid().ToString("N"), "durable.db");

    private static AiRunRecord CreateRun(string id, string prNodeId) =>
        new(
            id,
            prNodeId,
            "headsha",
            "basesha",
            null,
            AiRunState.Running,
            0,
            null,
            $"cache-{id}",
            null,
            DateTimeOffset.UtcNow,
            null);
}

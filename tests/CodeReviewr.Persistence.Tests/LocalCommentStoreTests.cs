using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Persistence;
using NUnit.Framework;

namespace CodeReviewr.Persistence.Tests;

public sealed class LocalCommentStoreTests
{
    [Test]
    public async Task Add_And_List_RoundTrips()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteLocalCommentStore(path);

        var created = await store.AddAsync(new LocalCommentCreate(
            "repo-a",
            "src/a.cs",
            10,
            12,
            DiffSide.New,
            "Please null-check here.",
            "blob-oid"));

        Assert.That(created.Id, Is.Not.Empty);
        Assert.That(created.IsResolved, Is.False);
        Assert.That(created.ContentId, Is.EqualTo("blob-oid"));

        var listed = await store.ListAsync("repo-a");
        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0].Id, Is.EqualTo(created.Id));
        Assert.That(listed[0].Path, Is.EqualTo("src/a.cs"));
        Assert.That(listed[0].StartLine, Is.EqualTo(10));
        Assert.That(listed[0].EndLine, Is.EqualTo(12));
        Assert.That(listed[0].Side, Is.EqualTo(DiffSide.New));
        Assert.That(listed[0].Body, Is.EqualTo("Please null-check here."));

        Assert.That(await store.ListAsync("repo-other"), Is.Empty);
    }

    [Test]
    public async Task CountUnresolved_And_SetResolved_Work()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteLocalCommentStore(path);

        var first = await store.AddAsync(new LocalCommentCreate(
            "repo-a", "src/a.cs", 1, 1, DiffSide.New, "one"));
        var second = await store.AddAsync(new LocalCommentCreate(
            "repo-a", "src/b.cs", 2, 2, DiffSide.Old, "two"));
        await store.AddAsync(new LocalCommentCreate(
            "repo-b", "src/c.cs", 3, 3, DiffSide.New, "other-repo"));

        Assert.That(await store.CountUnresolvedAsync("repo-a"), Is.EqualTo(2));

        await store.SetResolvedAsync(first.Id, true);
        Assert.That(await store.CountUnresolvedAsync("repo-a"), Is.EqualTo(1));

        var listed = await store.ListAsync("repo-a");
        Assert.That(listed.Single(c => c.Id == first.Id).IsResolved, Is.True);
        Assert.That(listed.Single(c => c.Id == second.Id).IsResolved, Is.False);

        await store.SetResolvedAsync(first.Id, false);
        Assert.That(await store.CountUnresolvedAsync("repo-a"), Is.EqualTo(2));
    }

    [Test]
    public async Task UpdateBody_And_Delete_Work()
    {
        var path = CreateDbPath();
        using var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();
        using var store = new SqliteLocalCommentStore(path);

        var created = await store.AddAsync(new LocalCommentCreate(
            "repo-a", "src/a.cs", 5, 6, DiffSide.New, "original"));

        await store.UpdateBodyAsync(created.Id, "revised");
        var listed = await store.ListAsync("repo-a");
        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0].Body, Is.EqualTo("revised"));
        Assert.That(listed[0].UpdatedUtc, Is.GreaterThanOrEqualTo(created.UpdatedUtc));

        await store.DeleteAsync(created.Id);
        Assert.That(await store.ListAsync("repo-a"), Is.Empty);
        Assert.That(await store.CountUnresolvedAsync("repo-a"), Is.EqualTo(0));
    }

    private static string CreateDbPath() =>
        Path.Combine(Path.GetTempPath(), "CodeReviewr.Tests", Guid.NewGuid().ToString("N"), "durable.db");
}

using CodeReviewr.Core;
using CodeReviewr.Core.Caching;
using CodeReviewr.Core.Diff;
using NUnit.Framework;

namespace CodeReviewr.Core.Tests;

public sealed class MemoryDiffCacheTests
{
    [Test]
    public void Hit_Increments_HitCount()
    {
        var cache = new MemoryDiffCache();
        var key = new FileDiffKey(ContentId.FromSha("a".PadRight(40, '0')), ContentId.FromSha("b".PadRight(40, '0')), DiffOptions.Default);
        var diff = new FileDiff(DiffTarget.IndexToWorktree.AsWorkingCopy(), FilePath.From("a.txt"), FilePath.From("a.txt"),
            ChangeKind.Modified, key.OldContent, key.NewContent, false, [], "");
        cache.Set(key, diff);
        Assert.That(cache.TryGet(key, out var got), Is.True);
        Assert.That(got, Is.SameAs(diff));
        Assert.That(cache.HitCount, Is.EqualTo(1));
        Assert.That(cache.MissCount, Is.EqualTo(0));
    }

    [Test]
    public void Set_Evicts_Least_Recently_Used_When_Over_Capacity()
    {
        var cache = new MemoryDiffCache(capacity: 2);
        var opts = DiffOptions.Default;
        var a = new FileDiffKey(ContentId.FromSha("a".PadRight(40, 'a')), ContentId.FromSha("b".PadRight(40, 'b')), opts);
        var b = new FileDiffKey(ContentId.FromSha("c".PadRight(40, 'c')), ContentId.FromSha("d".PadRight(40, 'd')), opts);
        var c = new FileDiffKey(ContentId.FromSha("e".PadRight(40, 'e')), ContentId.FromSha("f".PadRight(40, 'f')), opts);

        var empty = new FileDiff(
            DiffTarget.IndexToWorktree.AsWorkingCopy(),
            FilePath.From("a"),
            FilePath.From("a"),
            ChangeKind.Modified,
            ContentId.Empty,
            ContentId.Empty,
            IsBinary: false,
            Hunks: [],
            RawPatch: "");

        cache.Set(a, empty);
        cache.Set(b, empty);
        Assert.That(cache.TryGet(a, out _), Is.True);

        cache.Set(c, empty);
        Assert.That(cache.Count, Is.EqualTo(2));
        Assert.That(cache.TryGet(a, out _), Is.True);
        Assert.That(cache.TryGet(b, out _), Is.False);
        Assert.That(cache.TryGet(c, out _), Is.True);
    }
}

public sealed class GitVersionTests
{
    [Test]
    public void MeetsMinimum_For_2_30()
    {
        Assert.That(new GitVersion(2, 30, 0, "2.30.0").MeetsMinimum, Is.True);
        Assert.That(new GitVersion(2, 29, 0, "2.29.0").MeetsMinimum, Is.False);
        Assert.That(new GitVersion(3, 0, 0, "3.0.0").MeetsMinimum, Is.True);
    }
}

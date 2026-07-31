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
        var diff = new FileDiff(DiffTarget.IndexToWorktree, FilePath.From("a.txt"), FilePath.From("a.txt"),
            ChangeKind.Modified, key.OldContent, key.NewContent, false, [], "");
        cache.Set(key, diff);
        Assert.That(cache.TryGet(key, out var got), Is.True);
        Assert.That(got, Is.SameAs(diff));
        Assert.That(cache.HitCount, Is.EqualTo(1));
        Assert.That(cache.MissCount, Is.EqualTo(0));
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

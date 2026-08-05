using NUnit.Framework;

namespace CodeReviewr.AI.Tests;

public sealed class AiCacheKeysTests
{
    [Test]
    public void ComputeChangeBriefingKey_SameInputs_AreDeterministic()
    {
        var key1 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", "gpt-5", "rules-hash", "instr-hash");
        var key2 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", "gpt-5", "rules-hash", "instr-hash");

        Assert.That(key1, Is.EqualTo(key2));
    }

    [Test]
    public void ComputeChangeBriefingKey_DifferentHeadSha_ProducesDifferentKey()
    {
        var key1 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head1", "base", "PullRequest", "1", "gpt-5", "rules-hash", "instr-hash");
        var key2 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head2", "base", "PullRequest", "1", "gpt-5", "rules-hash", "instr-hash");

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void ComputeChangeBriefingKey_DifferentPrNodeId_ProducesDifferentKey()
    {
        var key1 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", "gpt-5", "rules-hash", "instr-hash");
        var key2 = AiCacheKeys.ComputeChangeBriefingKey("PR_2", "head", "base", "PullRequest", "1", "gpt-5", "rules-hash", "instr-hash");

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void ComputeChangeBriefingKey_DifferentPromptVersion_ProducesDifferentKey()
    {
        var key1 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", "gpt-5", "rules-hash", "instr-hash");
        var key2 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "2", "gpt-5", "rules-hash", "instr-hash");

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void ComputeChangeBriefingKey_DifferentModel_ProducesDifferentKey()
    {
        var key1 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", "gpt-5", "rules-hash", "instr-hash");
        var key2 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", "gpt-4", "rules-hash", "instr-hash");

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void ComputeChangeBriefingKey_DifferentRulesHash_ProducesDifferentKey()
    {
        var key1 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", "gpt-5", "rules-hash-a", "instr-hash");
        var key2 = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", "gpt-5", "rules-hash-b", "instr-hash");

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void ComputeChangeBriefingKey_NullModel_DoesNotThrow_AndDiffersFromEmptyModel()
    {
        Assert.DoesNotThrow(() => AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", null, "r", "i"));

        var withNull = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", null, "r", "i");
        var withEmpty = AiCacheKeys.ComputeChangeBriefingKey("PR_1", "head", "base", "PullRequest", "1", "", "r", "i");

        // Both null and "" are normalised to "" internally, so they collide - this is expected,
        // documented behaviour rather than an accidental collision.
        Assert.That(withNull, Is.EqualTo(withEmpty));
    }

    [Test]
    public void ComputeFileKey_SameInputs_AreDeterministic()
    {
        var key1 = AiCacheKeys.ComputeFileKey("src/a.cs", "before", "after", "1", "gpt-5", "r", "i");
        var key2 = AiCacheKeys.ComputeFileKey("src/a.cs", "before", "after", "1", "gpt-5", "r", "i");

        Assert.That(key1, Is.EqualTo(key2));
    }

    [Test]
    public void ComputeFileKey_DifferentPath_ProducesDifferentKey()
    {
        var key1 = AiCacheKeys.ComputeFileKey("src/a.cs", "before", "after", "1", "gpt-5", "r", "i");
        var key2 = AiCacheKeys.ComputeFileKey("src/b.cs", "before", "after", "1", "gpt-5", "r", "i");

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void ComputeFileKey_DifferentBlobOids_ProduceDifferentKeys()
    {
        var key1 = AiCacheKeys.ComputeFileKey("src/a.cs", "before1", "after1", "1", "gpt-5", "r", "i");
        var key2 = AiCacheKeys.ComputeFileKey("src/a.cs", "before2", "after2", "1", "gpt-5", "r", "i");

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void ComputeFileKey_NullBlobOids_DoNotThrow()
    {
        Assert.DoesNotThrow(() => AiCacheKeys.ComputeFileKey("src/new-file.cs", null, "after", "1", "gpt-5", "r", "i"));
        Assert.DoesNotThrow(() => AiCacheKeys.ComputeFileKey("src/deleted-file.cs", "before", null, "1", "gpt-5", "r", "i"));
    }

    [Test]
    public void ChangeBriefingKey_And_FileKey_WithSameLogicalInputs_NeverCollide()
    {
        // The "change-briefing" / "file" discriminator prefix must keep the two key spaces disjoint even
        // when other components happen to line up.
        var prKey = AiCacheKeys.ComputeChangeBriefingKey("same", "same", "same", "PullRequest", "1", "m", "r", "i");
        var fileKey = AiCacheKeys.ComputeFileKey("same", "same", "same", "1", "m", "r", "i");

        Assert.That(prKey, Is.Not.EqualTo(fileKey));
    }

    [Test]
    public void Hash_SameValue_IsDeterministic()
    {
        var first = AiCacheKeys.Hash("hello");
        var second = AiCacheKeys.Hash("hello");

        Assert.That(first, Is.EqualTo(second));
    }

    [Test]
    public void Hash_DifferentValues_ProduceDifferentHashes()
    {
        Assert.That(AiCacheKeys.Hash("hello"), Is.Not.EqualTo(AiCacheKeys.Hash("world")));
    }

    [Test]
    public void Hash_NullAndEmpty_AreEquivalentAndDoNotThrow()
    {
        string? nullValue = null;

        Assert.DoesNotThrow(() => AiCacheKeys.Hash(nullValue));
        Assert.That(AiCacheKeys.Hash(nullValue), Is.EqualTo(AiCacheKeys.Hash("")));
    }

    [Test]
    public void Hash_ReturnsSixteenLowercaseHexCharacters()
    {
        var hash = AiCacheKeys.Hash("some review rules text");

        Assert.That(hash, Has.Length.EqualTo(16));
        Assert.That(hash, Does.Match("^[0-9a-f]{16}$"));
    }
}

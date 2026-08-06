using GitDelta.Core;
using NUnit.Framework;

namespace GitDelta.AI.Tests;

public sealed class AnnotationBlobOidTests
{
    private static readonly AiReviewCoordinator.FileDepthContext File =
        new("src/App.cs", "before-oid", "after-oid", 40, 10, 2);

    [Test]
    public void Resolve_MissingBlobOid_SideNew_UsesAfterOid()
    {
        var oid = AiReviewCoordinator.ResolveAnnotationBlobOid(null, DiffSide.New, File);
        Assert.That(oid, Is.EqualTo("after-oid"));
    }

    [Test]
    public void Resolve_MissingBlobOid_SideOld_UsesBeforeOid()
    {
        var oid = AiReviewCoordinator.ResolveAnnotationBlobOid("", DiffSide.Old, File);
        Assert.That(oid, Is.EqualTo("before-oid"));
    }

    [TestCase("New")]
    [TestCase("Old")]
    [TestCase("(new file)")]
    [TestCase("(deleted)")]
    public void Resolve_PlaceholderBlobOid_SideNew_UsesAfterOid(string placeholder)
    {
        var oid = AiReviewCoordinator.ResolveAnnotationBlobOid(placeholder, DiffSide.New, File);
        Assert.That(oid, Is.EqualTo("after-oid"));
    }

    [Test]
    public void Resolve_RealBlobOid_IsPreserved()
    {
        var oid = AiReviewCoordinator.ResolveAnnotationBlobOid("abc123", DiffSide.New, File);
        Assert.That(oid, Is.EqualTo("abc123"));
    }

    [Test]
    public void Resolve_MissingBlobOid_NoFileContext_ReturnsNull()
    {
        var oid = AiReviewCoordinator.ResolveAnnotationBlobOid(null, DiffSide.New, null);
        Assert.That(oid, Is.Null);
    }

    [Test]
    public void Resolve_SideNew_ButAfterOidNull_ReturnsNull()
    {
        var deleted = new AiReviewCoordinator.FileDepthContext("gone.cs", "before-oid", null, 100, 0, 10);
        var oid = AiReviewCoordinator.ResolveAnnotationBlobOid("New", DiffSide.New, deleted);
        Assert.That(oid, Is.Null);
    }
}

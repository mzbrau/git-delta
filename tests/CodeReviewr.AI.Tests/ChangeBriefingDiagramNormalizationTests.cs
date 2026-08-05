using NUnit.Framework;

namespace CodeReviewr.AI.Tests;

public sealed class ChangeBriefingDiagramNormalizationTests
{
    [Test]
    public void NormalizeDiagramMermaid_NullOrWhitespace_ReturnsNull()
    {
        Assert.That(AiReviewCoordinator.NormalizeDiagramMermaid(null), Is.Null);
        Assert.That(AiReviewCoordinator.NormalizeDiagramMermaid(""), Is.Null);
        Assert.That(AiReviewCoordinator.NormalizeDiagramMermaid("   "), Is.Null);
    }

    [Test]
    public void NormalizeDiagramMermaid_PlainSource_IsTrimmed()
    {
        var result = AiReviewCoordinator.NormalizeDiagramMermaid("  flowchart TD\n  A-->B  ");
        Assert.That(result, Is.EqualTo("flowchart TD\n  A-->B"));
    }

    [Test]
    public void NormalizeDiagramMermaid_StripsMarkdownFences()
    {
        var fenced = """
            ```mermaid
            flowchart TD
              A-->B
            ```
            """;

        var result = AiReviewCoordinator.NormalizeDiagramMermaid(fenced);

        Assert.That(result, Is.EqualTo("flowchart TD\n  A-->B"));
    }

    [Test]
    public void NormalizeDiagramMermaid_FenceOnly_ReturnsNull()
    {
        Assert.That(AiReviewCoordinator.NormalizeDiagramMermaid("```mermaid\n```"), Is.Null);
    }
}

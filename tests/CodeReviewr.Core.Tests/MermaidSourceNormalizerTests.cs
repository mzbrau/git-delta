using CodeReviewr.Core.AI;
using NUnit.Framework;

namespace CodeReviewr.Core.Tests;

public sealed class MermaidSourceNormalizerTests
{
    [Test]
    public void Normalize_NullOrWhitespace_ReturnsNull()
    {
        Assert.That(MermaidSourceNormalizer.Normalize(null), Is.Null);
        Assert.That(MermaidSourceNormalizer.Normalize(""), Is.Null);
        Assert.That(MermaidSourceNormalizer.Normalize("   "), Is.Null);
    }

    [Test]
    public void Normalize_PlainSource_IsTrimmed()
    {
        var result = MermaidSourceNormalizer.Normalize("  flowchart TD\n  A-->B  ");
        Assert.That(result, Is.EqualTo("flowchart TD\n  A-->B"));
    }

    [Test]
    public void Normalize_StripsMarkdownFences()
    {
        var fenced = """
            ```mermaid
            flowchart TD
              A-->B
            ```
            """;

        var result = MermaidSourceNormalizer.Normalize(fenced);

        Assert.That(result, Is.EqualTo("flowchart TD\n  A-->B"));
    }

    [Test]
    public void Normalize_FenceOnly_ReturnsNull()
    {
        Assert.That(MermaidSourceNormalizer.Normalize("```mermaid\n```"), Is.Null);
    }

    [Test]
    public void Normalize_SequenceDiagram_StripsFences()
    {
        var source = """
            ```mermaid
            sequenceDiagram
              A->>B: hi
            ```
            """;

        var result = MermaidSourceNormalizer.Normalize(source);

        Assert.That(result, Is.EqualTo("sequenceDiagram\n  A->>B: hi"));
    }
}

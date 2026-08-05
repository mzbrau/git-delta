using CodeReviewr.Core.AI;
using NUnit.Framework;

namespace CodeReviewr.App.Tests;

public sealed class MermaidDiagramViewTests
{
    [Test]
    public void NormalizeSource_StripsFences_AndTrims()
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

    [Test]
    public void NormalizeSource_Blank_ReturnsNull()
    {
        Assert.That(MermaidSourceNormalizer.Normalize(null), Is.Null);
        Assert.That(MermaidSourceNormalizer.Normalize("  "), Is.Null);
    }
}

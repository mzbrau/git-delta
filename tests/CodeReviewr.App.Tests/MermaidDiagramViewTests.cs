using CodeReviewr.App.Controls;
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

        var result = MermaidDiagramView.NormalizeSource(source);

        Assert.That(result, Is.EqualTo("sequenceDiagram\n  A->>B: hi"));
    }

    [Test]
    public void NormalizeSource_Blank_ReturnsNull()
    {
        Assert.That(MermaidDiagramView.NormalizeSource(null), Is.Null);
        Assert.That(MermaidDiagramView.NormalizeSource("  "), Is.Null);
    }
}

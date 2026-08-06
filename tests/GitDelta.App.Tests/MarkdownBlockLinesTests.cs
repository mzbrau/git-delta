using GitDelta.App.Controls;
using Markdig;
using Markdig.Syntax;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class MarkdownBlockLinesTests
{
    [Test]
    public void GetRange_SingleLine_Heading()
    {
        const string markdown = "# Title\n";
        var heading = ParseFirstBlock(markdown);

        var (start, end) = MarkdownBlockLines.GetRange(heading, markdown);

        Assert.That(start, Is.EqualTo(1));
        Assert.That(end, Is.EqualTo(1));
    }

    [Test]
    public void GetRange_MultiLine_Paragraph()
    {
        const string markdown = "line one\nline two\nline three\n";
        var paragraph = ParseFirstBlock(markdown);

        var (start, end) = MarkdownBlockLines.GetRange(paragraph, markdown);

        Assert.That(start, Is.EqualTo(1));
        Assert.That(end, Is.EqualTo(3));
    }

    [Test]
    public void GetRange_SecondBlock_Uses_Source_Line()
    {
        const string markdown = "# Title\n\nBody paragraph.\n";
        var document = Markdown.Parse(markdown, MarkdownRendererPipeline());
        var blocks = document.Cast<Block>().ToList();
        Assert.That(blocks, Has.Count.GreaterThanOrEqualTo(2));

        var (start, end) = MarkdownBlockLines.GetRange(blocks[1], markdown);

        Assert.That(start, Is.EqualTo(3));
        Assert.That(end, Is.EqualTo(3));
    }

    private static Block ParseFirstBlock(string markdown)
    {
        var document = Markdown.Parse(markdown, MarkdownRendererPipeline());
        return document.Cast<Block>().First();
    }

    private static MarkdownPipeline MarkdownRendererPipeline() =>
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
}

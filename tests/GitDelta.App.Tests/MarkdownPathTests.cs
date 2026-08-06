using GitDelta.App.Controls;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class MarkdownPathTests
{
    [TestCase("README.md")]
    [TestCase("docs/guide.MD")]
    [TestCase("notes.markdown")]
    [TestCase("a/b/c.MARKDOWN")]
    public void IsMarkdownPath_True_For_Markdown_Extensions(string path) =>
        Assert.That(MarkdownPath.IsMarkdownPath(path), Is.True);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Program.cs")]
    [TestCase("readme.txt")]
    [TestCase("file.mdx")]
    [TestCase("file.md.bak")]
    public void IsMarkdownPath_False_For_NonMarkdown(string? path) =>
        Assert.That(MarkdownPath.IsMarkdownPath(path), Is.False);
}

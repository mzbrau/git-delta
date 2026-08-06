using Markdig;

namespace GitDelta.App.Controls;

/// <summary>
/// Shared Markdig pipeline used to split markdown into top-level blocks for
/// <see cref="MarkdownFilePreview"/> line mapping. Painting is done by LiveMarkdown.
/// </summary>
internal static class MarkdownDocumentParser
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();
}

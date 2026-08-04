using System.Collections.Concurrent;
using System.Reflection;

namespace CodeReviewr.AI;

/// <summary>
/// Loads and renders the embedded prompt templates under <c>Prompts/</c>. Placeholders use
/// <c>{{name}}</c> syntax and are substituted with plain string replacement — templates are
/// authored by us, not by untrusted input, so no templating engine is needed.
/// </summary>
public sealed class AiPromptCatalog
{
    /// <summary>Bumped whenever prompt wording changes in a way that should invalidate cached results.</summary>
    public const string PromptVersion = "4";

    private readonly Assembly _assembly = typeof(AiPromptCatalog).Assembly;
    private readonly ConcurrentDictionary<string, string> _resourceCache = new();


    public string GetFileSummaryPrompt(IReadOnlyDictionary<string, string> placeholders) =>
        Render("file_summary.md", placeholders);

    public string GetAnnotationPrompt(IReadOnlyDictionary<string, string> placeholders) =>
        Render("annotation.md", placeholders);

    public string GetExplanationPrompt(IReadOnlyDictionary<string, string> placeholders) =>
        Render("explanation.md", placeholders);

    public string GetCommentSuggestionPrompt(IReadOnlyDictionary<string, string> placeholders) =>
        Render("comment_suggestion.md", placeholders);

    public string GetChatSystemMessage(IReadOnlyDictionary<string, string>? placeholders = null) =>
        Render("chat_system.md", placeholders ?? new Dictionary<string, string>());

    public string GetDefaultReviewRules() => LoadResource("default_rules.md");

    private string Render(string resourceFileName, IReadOnlyDictionary<string, string> placeholders)
    {
        var template = LoadResource(resourceFileName);
        foreach (var (key, value) in placeholders)
            template = template.Replace("{{" + key + "}}", value);

        return template;
    }

    private string LoadResource(string fileName) =>
        _resourceCache.GetOrAdd(fileName, static (name, assembly) =>
        {
            var resourceName = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
                throw new InvalidOperationException($"Embedded prompt resource '{name}' was not found.");

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }, _assembly);
}

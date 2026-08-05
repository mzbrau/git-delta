using NUnit.Framework;

namespace CodeReviewr.AI.Tests;

public sealed class AiPromptCatalogTests
{
    [Test]
    public void PromptVersion_IsNonEmpty()
    {
        Assert.That(AiPromptCatalog.PromptVersion, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void GetFileBriefingPrompt_RendersAllPlaceholders()
    {
        var catalog = new AiPromptCatalog();

        var prompt = catalog.GetFileBriefingPrompt(new Dictionary<string, string>
        {
            ["rules"] = "RULE-TEXT",
            ["adhoc_instructions"] = "ADHOC-TEXT",
            ["path"] = "src/App.cs",
            ["before_oid"] = "before123",
            ["after_oid"] = "after456",
            ["change_percent"] = "42",
            ["lines_added"] = "10",
            ["lines_removed"] = "2",
        });

        Assert.That(prompt, Does.Contain("src/App.cs"));
        Assert.That(prompt, Does.Contain("before123"));
        Assert.That(prompt, Does.Contain("after456"));
        Assert.That(prompt, Does.Contain("42"));
        Assert.That(prompt, Does.Contain("add_annotation"));
        Assert.That(prompt, Does.Contain("1–5"));
        Assert.That(prompt, Does.Contain("\"blobOid\": \"after456\""));
        Assert.That(prompt, Does.Contain("Never use `New`, `Old`"));
        Assert.That(prompt, Does.Contain("submit_file_briefing"));
        Assert.That(prompt, Does.Not.Contain("{{"));
    }

    [Test]
    public void GetChangeBriefingPrompt_RendersAllPlaceholders()
    {
        var catalog = new AiPromptCatalog();

        var prompt = catalog.GetChangeBriefingPrompt(new Dictionary<string, string>
        {
            ["rules"] = "RULE-TEXT",
            ["facts"] = "FACTS-TEXT",
            ["adhoc_instructions"] = "ADHOC-TEXT",
        });

        Assert.That(prompt, Does.Contain("RULE-TEXT"));
        Assert.That(prompt, Does.Contain("FACTS-TEXT"));
        Assert.That(prompt, Does.Contain("ADHOC-TEXT"));
        Assert.That(prompt, Does.Contain("submit_change_briefing"));
        Assert.That(prompt, Does.Contain("diagramMermaid"));
        Assert.That(prompt, Does.Not.Contain("{{"));
    }

    [Test]
    public void GetExplanationPrompt_RendersPlaceholders()
    {
        var catalog = new AiPromptCatalog();

        var prompt = catalog.GetExplanationPrompt(new Dictionary<string, string>
        {
            ["context"] = "File: src/App.cs",
            ["question"] = "Why was this changed?",
        });

        Assert.That(prompt, Does.Contain("Why was this changed?"));
        Assert.That(prompt, Does.Not.Contain("{{"));
    }

    [Test]
    public void GetCommentSuggestionPrompt_RendersPlaceholders()
    {
        var catalog = new AiPromptCatalog();

        var prompt = catalog.GetCommentSuggestionPrompt(new Dictionary<string, string>
        {
            ["context"] = "File: src/App.cs",
            ["action"] = "explain",
            ["selection"] = "var x = 1;",
        });

        Assert.That(prompt, Does.Contain("var x = 1;"));
        Assert.That(prompt, Does.Not.Contain("{{"));
    }

    [Test]
    public void GetChatSystemMessage_WithNoPlaceholders_DoesNotThrow()
    {
        var catalog = new AiPromptCatalog();

        Assert.DoesNotThrow(() => catalog.GetChatSystemMessage());
    }

    [Test]
    public void GetDefaultReviewRules_IsNonEmpty()
    {
        var catalog = new AiPromptCatalog();

        var rules = catalog.GetDefaultReviewRules();

        Assert.That(rules, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Render_IsCached_AcrossCalls()
    {
        var catalog = new AiPromptCatalog();
        var placeholders = new Dictionary<string, string>
        {
            ["rules"] = "R",
            ["adhoc_instructions"] = "A",
            ["path"] = "p",
            ["before_oid"] = "b",
            ["after_oid"] = "a",
            ["change_percent"] = "10",
            ["lines_added"] = "1",
            ["lines_removed"] = "0",
        };

        var first = catalog.GetFileBriefingPrompt(placeholders);
        var second = catalog.GetFileBriefingPrompt(placeholders);

        Assert.That(first, Is.EqualTo(second));
    }
}

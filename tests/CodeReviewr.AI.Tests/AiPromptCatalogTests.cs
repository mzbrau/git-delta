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
    public void GetTriagePrompt_RendersAllPlaceholders()
    {
        var catalog = new AiPromptCatalog();

        var prompt = catalog.GetTriagePrompt(new Dictionary<string, string>
        {
            ["rules"] = "RULE-TEXT",
            ["facts"] = "FACTS-TEXT",
            ["adhoc_instructions"] = "ADHOC-TEXT",
        });

        Assert.That(prompt, Does.Contain("RULE-TEXT"));
        Assert.That(prompt, Does.Contain("FACTS-TEXT"));
        Assert.That(prompt, Does.Contain("ADHOC-TEXT"));
        Assert.That(prompt, Does.Not.Contain("{{"));
        Assert.That(prompt, Does.Contain("submit_pr_triage"));
    }

    [Test]
    public void GetFileSummaryPrompt_RendersAllPlaceholders()
    {
        var catalog = new AiPromptCatalog();

        var prompt = catalog.GetFileSummaryPrompt(new Dictionary<string, string>
        {
            ["rules"] = "RULE-TEXT",
            ["adhoc_instructions"] = "ADHOC-TEXT",
            ["path"] = "src/App.cs",
            ["before_oid"] = "before123",
            ["after_oid"] = "after456",
        });

        Assert.That(prompt, Does.Contain("src/App.cs"));
        Assert.That(prompt, Does.Contain("before123"));
        Assert.That(prompt, Does.Contain("after456"));
        Assert.That(prompt, Does.Contain("add_annotation"));
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
        // Not observable from the outside beyond "doesn't throw and returns the same content
        // twice", but guards against the embedded-resource cache silently corrupting output.
        var catalog = new AiPromptCatalog();
        var placeholders = new Dictionary<string, string> { ["rules"] = "R", ["facts"] = "F", ["adhoc_instructions"] = "A" };

        var first = catalog.GetTriagePrompt(placeholders);
        var second = catalog.GetTriagePrompt(placeholders);

        Assert.That(first, Is.EqualTo(second));
    }

    [Test]
    public Task TriagePrompt_MatchesSnapshot()
    {
        var catalog = new AiPromptCatalog();

        var prompt = catalog.GetTriagePrompt(new Dictionary<string, string>
        {
            ["rules"] = "- Prioritize correctness and security.\n- Flag missing tests.",
            ["facts"] = "Title: Add login retry\nFiles changed: 2 (+10 / -2)",
            ["adhoc_instructions"] = "Pay extra attention to error handling.",
        });

        return Verify(prompt);
    }
}

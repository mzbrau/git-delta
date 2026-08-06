using System.Text.Json;
using GitDelta.Core;
using GitDelta.Core.Settings;
using NUnit.Framework;

namespace GitDelta.Core.Tests;

public sealed class SettingsTokenIsolationTests
{
    [Test]
    public void AppSettings_Json_Never_Contains_Token_Fields()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new GitHubAccountSettings
                {
                    Host = "github.com",
                    Login = "octocat",
                    // If a future regression adds Token to settings, serialization must still not persist secrets.
                },
            ],
        };

        var json = JsonSerializer.Serialize(settings);
        Assert.That(json, Does.Not.Contain("\"Token\""));
        Assert.That(json, Does.Not.Contain("gho_"));
        Assert.That(json, Does.Not.Contain("ghp_"));
        Assert.That(json, Does.Not.Contain("\"AccessToken\""));
        Assert.That(json, Does.Not.Contain("\"PersonalAccessToken\""));
    }

    [Test]
    public void JsonSettingsStore_Clone_Preserves_Ai_Settings()
    {
        var store = new JsonSettingsStore();
        store.Update(s =>
        {
            s.AiAssistanceEnabled = true;
            s.AiTurnBudget = 40;
            s.AiReviewRules = "Focus on security";
            s.AiPathDenylist = ["secrets/**"];
            s.AiExcludedRepositories = ["sensitive/repo"];
            s.AiDisclosureAcknowledged = true;
            s.MaxDiffPatchBytes = 1_000_000;
            s.DiffCacheCapacity = 32;
        });

        Assert.That(store.Current.AiAssistanceEnabled, Is.True);
        Assert.That(store.Current.AiTurnBudget, Is.EqualTo(40));
        Assert.That(store.Current.AiReviewRules, Is.EqualTo("Focus on security"));
        Assert.That(store.Current.AiPathDenylist, Is.EqualTo(new[] { "secrets/**" }));
        Assert.That(store.Current.AiExcludedRepositories, Is.EqualTo(new[] { "sensitive/repo" }));
        Assert.That(store.Current.AiDisclosureAcknowledged, Is.True);
        Assert.That(store.Current.MaxDiffPatchBytes, Is.EqualTo(1_000_000));
        Assert.That(store.Current.DiffCacheCapacity, Is.EqualTo(32));
    }
}

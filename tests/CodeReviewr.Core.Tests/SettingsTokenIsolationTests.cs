using System.Text.Json;
using CodeReviewr.Core;
using CodeReviewr.Core.Settings;
using NUnit.Framework;

namespace CodeReviewr.Core.Tests;

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
        Assert.That(json, Does.Not.Contain("Token"));
        Assert.That(json, Does.Not.Contain("gho_"));
        Assert.That(json, Does.Not.Contain("ghp_"));
    }

    [Test]
    public void JsonSettingsStore_Clone_Preserves_Ai_Privacy_Flags()
    {
        var store = new JsonSettingsStore();
        store.Update(s =>
        {
            s.AiAssistanceEnabled = true;
            s.AiRedactSecrets = false;
            s.MaxDiffPatchBytes = 1_000_000;
            s.DiffCacheCapacity = 32;
        });

        Assert.That(store.Current.AiAssistanceEnabled, Is.True);
        Assert.That(store.Current.AiRedactSecrets, Is.False);
        Assert.That(store.Current.MaxDiffPatchBytes, Is.EqualTo(1_000_000));
        Assert.That(store.Current.DiffCacheCapacity, Is.EqualTo(32));
    }
}

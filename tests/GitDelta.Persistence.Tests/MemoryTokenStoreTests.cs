using GitDelta.Persistence;
using NUnit.Framework;

namespace GitDelta.Persistence.Tests;

public sealed class MemoryTokenStoreTests
{
    [Test]
    public async Task RoundTrip_SetGetDelete_Works()
    {
        var store = new MemoryTokenStore();

        await store.SetTokenAsync("github.com", "octocat", "ghp_test_token");
        var token = await store.GetTokenAsync("github.com", "octocat");

        Assert.That(token, Is.EqualTo("ghp_test_token"));

        await store.DeleteTokenAsync("github.com", "octocat");
        var deleted = await store.GetTokenAsync("github.com", "octocat");

        Assert.That(deleted, Is.Null);
    }
}

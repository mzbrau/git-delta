using System.Text.Json;
using GitDelta.GitHub;
using NUnit.Framework;

namespace GitDelta.GitHub.Tests;

public sealed class MentionableUsersParserTests
{
    [Test]
    public void Parse_MapsLoginAndAvatar()
    {
        const string json = """
            {
              "repository": {
                "mentionableUsers": {
                  "nodes": [
                    { "login": "alice", "avatarUrl": "https://example.com/a.png" },
                    { "login": "bob", "avatarUrl": null },
                    { "login": "", "avatarUrl": "https://example.com/skip.png" }
                  ]
                }
              }
            }
            """;

        using var doc = JsonDocument.Parse(json);
        var users = MentionableUsersParser.Parse(doc.RootElement);

        Assert.That(users, Has.Count.EqualTo(2));
        Assert.That(users[0].Login, Is.EqualTo("alice"));
        Assert.That(users[0].AvatarUrl, Is.EqualTo("https://example.com/a.png"));
        Assert.That(users[1].Login, Is.EqualTo("bob"));
        Assert.That(users[1].AvatarUrl, Is.Null);
    }

    [Test]
    public void Parse_MissingRepository_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{ "repository": null }""");
        var users = MentionableUsersParser.Parse(doc.RootElement);
        Assert.That(users, Is.Empty);
    }
}

using System.Text.Json;

namespace CodeReviewr.GitHub;

public static class MentionableUsersParser
{
    public static IReadOnlyList<MentionableUser> Parse(JsonElement data)
    {
        if (!data.TryGetProperty("repository", out var repo) ||
            repo.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            !repo.TryGetProperty("mentionableUsers", out var users) ||
            !users.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<MentionableUser>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("login", out var loginEl))
                continue;
            var login = loginEl.GetString();
            if (string.IsNullOrWhiteSpace(login))
                continue;
            var avatar = node.TryGetProperty("avatarUrl", out var avatarEl)
                ? avatarEl.GetString()
                : null;
            list.Add(new MentionableUser(login, avatar));
        }

        return list;
    }
}

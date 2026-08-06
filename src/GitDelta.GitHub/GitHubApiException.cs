namespace GitDelta.GitHub;

public sealed class GitHubApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

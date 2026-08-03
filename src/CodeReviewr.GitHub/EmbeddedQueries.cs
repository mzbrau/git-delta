using System.Reflection;

namespace CodeReviewr.GitHub;

internal static class EmbeddedQueries
{
    private static readonly Lazy<string> Viewer = new(() => Load("Viewer.graphql"));
    private static readonly Lazy<string> InboxSearch = new(() => Load("InboxSearch.graphql"));
    private static readonly Lazy<string> PullRequestDetail = new(() => Load("PullRequestDetail.graphql"));
    private static readonly Lazy<string> PullRequestThreads = new(() => Load("PullRequestThreads.graphql"));
    private static readonly Lazy<string> MentionableUsers = new(() => Load("MentionableUsers.graphql"));
    private static readonly Lazy<string> CapabilityProbe = new(() => Load("CapabilityProbe.graphql"));
    private static readonly Lazy<string> PendingReview = new(() => Load("PendingReviewQuery.graphql"));
    private static readonly Lazy<string> AddPullRequestReviewThread = new(() => Load("AddPullRequestReviewThread.graphql"));
    private static readonly Lazy<string> AddPullRequestReviewComment = new(() => Load("AddPullRequestReviewComment.graphql"));
    private static readonly Lazy<string> AddPullRequestReviewThreadReply = new(() => Load("AddPullRequestReviewThreadReply.graphql"));
    private static readonly Lazy<string> AddPullRequestReview = new(() => Load("AddPullRequestReview.graphql"));
    private static readonly Lazy<string> UpdatePullRequestReviewComment = new(() => Load("UpdatePullRequestReviewComment.graphql"));
    private static readonly Lazy<string> DeletePullRequestReviewComment = new(() => Load("DeletePullRequestReviewComment.graphql"));
    private static readonly Lazy<string> ResolveReviewThread = new(() => Load("ResolveReviewThread.graphql"));
    private static readonly Lazy<string> UnresolveReviewThread = new(() => Load("UnresolveReviewThread.graphql"));
    private static readonly Lazy<string> MarkFileAsViewed = new(() => Load("MarkFileAsViewed.graphql"));
    private static readonly Lazy<string> UnmarkFileAsViewed = new(() => Load("UnmarkFileAsViewed.graphql"));
    private static readonly Lazy<string> SubmitPullRequestReview = new(() => Load("SubmitPullRequestReview.graphql"));

    public static string ViewerQuery => Viewer.Value;
    public static string InboxSearchQuery => InboxSearch.Value;
    public static string PullRequestDetailQuery => PullRequestDetail.Value;
    public static string PullRequestThreadsQuery => PullRequestThreads.Value;
    public static string MentionableUsersQuery => MentionableUsers.Value;
    public static string CapabilityProbeQuery => CapabilityProbe.Value;
    public static string PendingReviewQuery => PendingReview.Value;
    public static string AddPullRequestReviewThreadMutation => AddPullRequestReviewThread.Value;
    public static string AddPullRequestReviewCommentMutation => AddPullRequestReviewComment.Value;
    public static string AddPullRequestReviewThreadReplyMutation => AddPullRequestReviewThreadReply.Value;
    public static string AddPullRequestReviewMutation => AddPullRequestReview.Value;
    public static string UpdatePullRequestReviewCommentMutation => UpdatePullRequestReviewComment.Value;
    public static string DeletePullRequestReviewCommentMutation => DeletePullRequestReviewComment.Value;
    public static string ResolveReviewThreadMutation => ResolveReviewThread.Value;
    public static string UnresolveReviewThreadMutation => UnresolveReviewThread.Value;
    public static string MarkFileAsViewedMutation => MarkFileAsViewed.Value;
    public static string UnmarkFileAsViewedMutation => UnmarkFileAsViewed.Value;
    public static string SubmitPullRequestReviewMutation => SubmitPullRequestReview.Value;

    private static string Load(string fileName)
    {
        var assembly = typeof(EmbeddedQueries).Assembly;
        var matches = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(fileName, StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected one embedded {fileName} resource, found {matches.Count}.");
        using var stream = assembly.GetManifestResourceStream(matches[0])
            ?? throw new InvalidOperationException($"Embedded {fileName} resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

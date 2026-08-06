using System.Collections.Concurrent;
using GitDelta.Core.AI;

namespace GitDelta.AI;

/// <summary>
/// Thread-safe registry of active AI review run state keyed by session.
/// </summary>
internal sealed class AiRunStateStore
{
    private readonly ConcurrentDictionary<string, AiActiveRunState> _runsBySession = new();

    public ICollection<AiActiveRunState> Values => _runsBySession.Values;

    public bool TryGet(string sessionKey, out AiActiveRunState context) =>
        _runsBySession.TryGetValue(sessionKey, out context!);

    public AiActiveRunState GetOrAdd(string sessionKey) =>
        _runsBySession.GetOrAdd(sessionKey, static _ => new AiActiveRunState());

    public AiActiveRunState Register(
        AiReviewRequest request,
        string runId,
        string? copilotSessionId,
        AiChangeBriefingResult? briefing,
        AiRunState state,
        string? model,
        string rulesHash,
        string instructionsHash)
    {
        var context = GetOrAdd(request.SessionKey);
        context.Request = request;
        context.SessionKey = request.SessionKey;
        context.RepositoryKey = request.RepositoryKey;
        context.RepositoryPath = request.RepositoryPath;
        context.HeadSha = request.HeadSha;
        context.MergeBaseSha = request.MergeBaseSha;
        context.AdHocInstructions = request.AdHocInstructions;
        context.RunId = runId;
        context.CopilotSessionId = copilotSessionId;
        context.ChangeBriefing = briefing;
        context.State = state;
        context.Model = model;
        context.RulesHash = rulesHash;
        context.InstructionsHash = instructionsHash;
        return context;
    }

    public void CancelRepository(string repositoryKey)
    {
        foreach (var context in _runsBySession.Values)
        {
            if (!string.Equals(context.RepositoryKey, repositoryKey, StringComparison.Ordinal))
                continue;

            context.UserCancelled = true;
            context.RunCts?.Cancel();
        }
    }

    public void Clear() => _runsBySession.Clear();

    public async ValueTask DisposeSessionsAsync()
    {
        foreach (var context in _runsBySession.Values)
        {
            if (context.Session is not null)
                await context.Session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

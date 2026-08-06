using System.Text;
using GitDelta.Core.Abstractions;
using GitDelta.Core.AI;

namespace GitDelta.AI;

/// <summary>
/// Chat / ask / inline-action helpers that build prompts and persist chat history.
/// Turn execution is injected so the coordinator keeps the agent session ownership.
/// </summary>
internal sealed class AiReviewChatActions(
    IAiResultStore resultStore,
    AiPromptCatalog prompts,
    Func<string, string, AiWorkPriority, CancellationToken, Task<string>> runTurnForText)
{
    private const int MaxChatHistoryMessages = 20;

    public Task<string> AskAsync(AiQuestionRequest request, CancellationToken ct = default) =>
        runTurnForText(request.SessionKey, BuildExplanationPrompt(request), AiWorkPriority.ExplicitUser, ct);

    public Task<string> RunInlineActionAsync(AiInlineActionRequest request, CancellationToken ct = default)
    {
        var placeholders = new Dictionary<string, string>
        {
            ["context"] = $"File: {request.Path}",
            ["action"] = request.Action,
            ["selection"] = request.SelectedLinesContext,
        };
        return runTurnForText(
            request.SessionKey,
            prompts.GetCommentSuggestionPrompt(placeholders),
            AiWorkPriority.ExplicitUser,
            ct);
    }

    public async Task<string> ChatAsync(AiQuestionRequest request, CancellationToken ct = default)
    {
        var prior = await resultStore.ListChatMessagesAsync(request.SessionKey, ct).ConfigureAwait(false);
        IReadOnlyList<AiChatMessage> cappedPrior = prior.Count <= MaxChatHistoryMessages
            ? prior
            : prior.Skip(prior.Count - MaxChatHistoryMessages).ToList();

        await resultStore.AppendChatMessageAsync(
            request.SessionKey, new AiChatMessage("user", request.Question, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        var answer = await runTurnForText(
                request.SessionKey,
                BuildExplanationPrompt(request, cappedPrior),
                AiWorkPriority.ExplicitUser,
                ct)
            .ConfigureAwait(false);

        await resultStore.AppendChatMessageAsync(
            request.SessionKey, new AiChatMessage("assistant", answer, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        return answer;
    }

    internal string BuildExplanationPrompt(
        AiQuestionRequest request,
        IReadOnlyList<AiChatMessage>? priorChat = null)
    {
        var context = new StringBuilder();
        if (!string.IsNullOrEmpty(request.Path))
        {
            context.AppendLine($"Currently selected file: {request.Path}");
            context.AppendLine(
                "The reviewer's question most likely relates to this file. Answer in that file's context unless the question clearly refers to something else.");
        }
        else
        {
            context.AppendLine("No file is currently selected. Treat this as a pull-request-wide question.");
        }

        if (!string.IsNullOrEmpty(request.SelectedLinesContext))
            context.AppendLine(request.SelectedLinesContext);

        if (priorChat is { Count: > 0 })
        {
            context.AppendLine();
            context.AppendLine("Conversation so far:");
            foreach (var message in priorChat)
                context.AppendLine($"{message.Role}: {message.Content}");
        }

        return prompts.GetExplanationPrompt(new Dictionary<string, string>
        {
            ["context"] = context.ToString(),
            ["question"] = request.Question,
        });
    }
}

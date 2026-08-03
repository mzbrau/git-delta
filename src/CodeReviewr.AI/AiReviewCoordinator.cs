using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeReviewr.AI.Agent;
using CodeReviewr.Core;
using CodeReviewr.Core.AI;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diff;
using Microsoft.Extensions.Logging;

namespace CodeReviewr.AI;

/// <summary>
/// Orchestrates Phase 3 AI review: gating on privacy settings, materialising a read-only working
/// tree, driving an agent session through triage/file-depth/chat turns via custom tools, and
/// persisting everything through <see cref="IAiResultStore"/> so results survive restarts.
/// </summary>
internal sealed class AiReviewCoordinator(
    ISettingsStore settingsStore,
    IAiResultStore resultStore,
    IAgentClient agentClient,
    ReviewTreeMaterialiser materialiser,
    AiPromptCatalog prompts,
    PrFactsAssembler factsAssembler,
    ITokenStore tokenStore,
    AiWorkQueue workQueue,
    ILogger<AiReviewCoordinator> logger) : IAIReviewService, IAsyncDisposable
{
    private const string DedicatedTokenHost = "copilot.github.com";
    private const string DedicatedTokenLogin = "dedicated-copilot-token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<string, RunContext> _runsByPr = new();
    private readonly ConcurrentDictionary<string, RepoObservers> _observersByRepo = new();
    private readonly ConcurrentDictionary<string, List<AiAnnotationResult>> _annotationsByBlob = new();

    // ---------------------------------------------------------------------
    // Cache-only reads (never touch the agent).
    // ---------------------------------------------------------------------

    public async ValueTask<AiRunSnapshot?> GetCachedRunAsync(string prNodeId, CancellationToken ct = default)
    {
        var run = await resultStore.GetLatestRunAsync(prNodeId, ct).ConfigureAwait(false);
        if (run is null)
            return null;

        var prResult = await resultStore.GetPrResultForRunAsync(run.Id, ct).ConfigureAwait(false);
        var triage = prResult is null ? null : Deserialize<AiPrTriageResult>(prResult.PayloadJson);
        return ToSnapshot(run, triage);
    }

    public async Task AttachCachedRunAsync(AiReviewRequest request, CancellationToken ct = default)
    {
        // Keep an already-live session; attach is only for hydrating after process restart.
        if (_runsByPr.TryGetValue(request.PrNodeId, out var existing) && existing.Session is not null)
            return;

        var run = await resultStore.GetLatestRunAsync(request.PrNodeId, ct).ConfigureAwait(false);
        if (run is null)
            return;

        var prResult = await resultStore.GetPrResultForRunAsync(run.Id, ct).ConfigureAwait(false);
        var triage = prResult is null ? null : Deserialize<AiPrTriageResult>(prResult.PayloadJson);
        var context = RegisterRunContext(request, run.Id, run.CopilotSessionId, triage, run.State);
        context.CacheKey = run.CacheKey;
        context.TurnsUsed = run.TurnsUsed;
        context.StartedUtc = run.StartedUtc;
        context.FinishedUtc = run.FinishedUtc;
        context.ErrorMessage = run.ErrorMessage;
        // Session intentionally left null — EnsureLiveSessionAsync resumes lazily.
    }

    public async ValueTask<IReadOnlyList<FilePath>> SuggestFileOrderAsync(
        string sessionKey,
        IReadOnlyList<FilePath> changedFiles,
        CancellationToken ct = default)
    {
        var cached = await GetCachedRunAsync(sessionKey, ct).ConfigureAwait(false);
        if (cached?.Triage is not { SuggestedOrder.Count: > 0 } triage)
            return changedFiles;

        var remaining = changedFiles.ToDictionary(f => f.Value, f => f, StringComparer.Ordinal);
        var ordered = new List<FilePath>(changedFiles.Count);
        foreach (var path in triage.SuggestedOrder)
        {
            if (remaining.Remove(path, out var file))
                ordered.Add(file);
        }

        ordered.AddRange(changedFiles.Where(f => remaining.ContainsKey(f.Value)));
        return ordered;
    }

    public async ValueTask<IReadOnlyList<AIChecklistItem>> GetChecklistAsync(string sessionKey, CancellationToken ct = default)
    {
        var cached = await GetCachedRunAsync(sessionKey, ct).ConfigureAwait(false);
        if (cached?.Triage is not { } triage)
            return [];

        var items = new List<AIChecklistItem>
        {
            new("risk", $"Overall risk: {triage.Risk}", triage.Summary, MapSeverity(triage.Risk)),
        };

        foreach (var justification in triage.Justifications)
        {
            items.Add(new AIChecklistItem(
                $"justification:{justification.FilePath}", justification.FilePath, justification.Reason, AIChecklistSeverity.Warning));
        }

        foreach (var file in triage.Files.Where(f => f.Classification == AiFileClassification.ReviewCarefully))
            items.Add(new AIChecklistItem($"file:{file.Path}", file.Path, file.Guidance, AIChecklistSeverity.Suggestion));

        return items;
    }

    public ValueTask<IReadOnlyList<IDiffAnnotation>> GetAnnotationsAsync(FileDiffKey key, CancellationToken ct = default)
    {
        var results = new List<IDiffAnnotation>();
        if (_annotationsByBlob.TryGetValue(key.OldContent.Value, out var oldAnnotations))
        {
            lock (oldAnnotations)
                results.AddRange(oldAnnotations.Select(ToDiffAnnotation));
        }

        if (_annotationsByBlob.TryGetValue(key.NewContent.Value, out var newAnnotations))
        {
            lock (newAnnotations)
                results.AddRange(newAnnotations.Select(ToDiffAnnotation));
        }

        return ValueTask.FromResult<IReadOnlyList<IDiffAnnotation>>(results);
    }

    public async ValueTask<AiFileSummaryResult?> GetFileSummaryAsync(string prNodeId, string path, CancellationToken ct = default)
    {
        var run = await resultStore.GetLatestRunAsync(prNodeId, ct).ConfigureAwait(false);
        if (run is null)
            return null;

        var files = await resultStore.ListFileResultsForRunAsync(run.Id, ct).ConfigureAwait(false);
        var match = files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));
        return match?.SummaryJson is null ? null : Deserialize<AiFileSummaryResult>(match.SummaryJson);
    }

    public async ValueTask<IReadOnlyList<AiAnnotationResult>> GetFileAnnotationsAsync(
        string prNodeId,
        string path,
        bool includeDismissed = false,
        CancellationToken ct = default)
    {
        var records = await resultStore.ListAnnotationsAsync(prNodeId, path, includeDismissed, ct).ConfigureAwait(false);
        return [.. records.Select(ToAnnotationResult)];
    }

    public Task SetAnnotationReadStateAsync(string annotationId, AiAnnotationReadState state, CancellationToken ct = default) =>
        resultStore.SetAnnotationReadStateAsync(annotationId, state, ct);

    public async ValueTask<IReadOnlyList<AiChatMessage>> GetChatHistoryAsync(string prNodeId, CancellationToken ct = default) =>
        await resultStore.ListChatMessagesAsync(prNodeId, ct).ConfigureAwait(false);

    public Task ClearChatHistoryAsync(string prNodeId, CancellationToken ct = default) =>
        resultStore.ClearChatMessagesAsync(prNodeId, ct);

    // ---------------------------------------------------------------------
    // Connectivity.
    // ---------------------------------------------------------------------

    public Task<AiConnectionProbeResult> TestConnectionAsync(CancellationToken ct = default) =>
        agentClient.ProbeAsync(ct);

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
        agentClient.ListModelsAsync(ct);

    // ---------------------------------------------------------------------
    // Run lifecycle.
    // ---------------------------------------------------------------------

    public async Task<AiRunSnapshot> StartReviewAsync(AiReviewRequest request, CancellationToken ct = default)
    {
        var gateReason = CheckGate(request.RepositoryKey);
        if (gateReason is not null)
            return FailedSnapshot(request, gateReason);

        var rulesHash = AiCacheKeys.Hash(EffectiveRules());
        var instructionsHash = AiCacheKeys.Hash(request.AdHocInstructions);
        var model = settingsStore.Current.AiModelOverride;
        var cacheKey = AiCacheKeys.ComputePrTriageKey(
            request.PrNodeId, request.HeadSha, request.MergeBaseSha, AiPromptCatalog.PromptVersion, model, rulesHash, instructionsHash);

        if (!request.DiscardCached)
        {
            var cachedResult = await resultStore.GetPrResultByCacheKeyAsync(cacheKey, ct).ConfigureAwait(false);
            if (cachedResult is not null)
            {
                var cachedRun = await resultStore.GetRunAsync(cachedResult.RunId, ct).ConfigureAwait(false);
                if (cachedRun is { State: AiRunState.Complete })
                {
                    var triage = Deserialize<AiPrTriageResult>(cachedResult.PayloadJson);
                    var context = RegisterRunContext(request, cachedRun.Id, cachedRun.CopilotSessionId, triage, AiRunState.Complete);
                    context.CacheKey = cacheKey;
                    return ToSnapshot(cachedRun, triage);
                }
            }
        }

        string? resumeSessionId = null;
        if (request.Resume)
        {
            var previousRun = await resultStore.GetLatestRunAsync(request.PrNodeId, ct).ConfigureAwait(false);
            resumeSessionId = previousRun?.CopilotSessionId;
        }

        var runId = Guid.NewGuid().ToString("N");
        var startedUtc = DateTimeOffset.UtcNow;
        var runContext = RegisterRunContext(request, runId, resumeSessionId, triage: null, AiRunState.Running);
        runContext.CacheKey = cacheKey;
        runContext.StartedUtc = startedUtc;
        runContext.UserCancelled = false;
        runContext.RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runTimeoutSeconds = Math.Max(30, settingsStore.Current.AiRunTimeoutSeconds);
        runContext.RunTimeoutSeconds = runTimeoutSeconds;
        runContext.RunCts.CancelAfter(TimeSpan.FromSeconds(runTimeoutSeconds));

        await resultStore.UpsertRunAsync(new AiRunRecord(
            runId, request.PrNodeId, request.HeadSha, request.MergeBaseSha, CopilotSessionId: resumeSessionId,
            AiRunState.Running, TurnsUsed: 0, request.AdHocInstructions, cacheKey, ErrorMessage: null,
            startedUtc, FinishedUtc: null), ct).ConfigureAwait(false);

        var completion = new TaskCompletionSource<AiRunSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        workQueue.Enqueue(new AiWorkItem(request.RepositoryKey, AiWorkPriority.Triage, Path: null, async workCt =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(workCt, runContext.RunCts.Token);
            try
            {
                var snapshot = await RunTriageAsync(request, runContext, linkedCts.Token).ConfigureAwait(false);
                completion.TrySetResult(snapshot);
            }
            catch (TimeoutException ex)
            {
                logger.LogError(ex, "AI triage timed out waiting for Copilot for PR {PrNodeId}.", request.PrNodeId);
                var turnTimeout = Math.Max(10, settingsStore.Current.AiTurnTimeoutSeconds);
                var message =
                    $"AI review timed out after {turnTimeout}s waiting for Copilot (turn timeout). {ex.Message}";
                var incomplete = await FinishRunAsync(runContext, AiRunState.Incomplete, message, CancellationToken.None)
                    .ConfigureAwait(false);
                completion.TrySetResult(incomplete);
            }
            catch (OperationCanceledException)
            {
                var message = runContext.UserCancelled
                    ? "The review was cancelled."
                    : $"AI review timed out after {runContext.RunTimeoutSeconds}s (run timeout).";
                var incomplete = await FinishRunAsync(runContext, AiRunState.Incomplete, message, CancellationToken.None)
                    .ConfigureAwait(false);
                completion.TrySetResult(incomplete);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI triage failed for PR {PrNodeId}.", request.PrNodeId);
                var failed = await FinishRunAsync(runContext, AiRunState.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
                completion.TrySetResult(failed);
            }
        }));

        return await completion.Task.ConfigureAwait(false);
    }

    public Task CancelAsync(string repositoryKey, CancellationToken ct = default)
    {
        workQueue.CancelRepository(repositoryKey);
        foreach (var context in _runsByPr.Values)
        {
            if (!string.Equals(context.RepositoryKey, repositoryKey, StringComparison.Ordinal))
                continue;

            context.UserCancelled = true;
            context.RunCts?.Cancel();
        }

        return Task.CompletedTask;
    }

    public IDisposable ObserveProgress(string repositoryKey, Action<AiRunProgress> handler) =>
        _observersByRepo.GetOrAdd(repositoryKey, static _ => new RepoObservers()).Add(handler);

    public async Task RequestFileDepthAsync(AiFileDepthRequest request, CancellationToken ct = default)
    {
        var context = await EnsureLiveSessionAsync(request.PrNodeId, ct).ConfigureAwait(false);
        var gateReason = CheckGate(context.RepositoryKey);
        if (gateReason is not null)
            throw new InvalidOperationException(gateReason);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workQueue.Enqueue(new AiWorkItem(context.RepositoryKey, AiWorkPriority.OpenFile, request.Path, async workCt =>
        {
            context.CurrentFileRequest = new FileDepthContext(request.Path, request.BeforeBlobOid, request.AfterBlobOid);
            try
            {
                NotifyProgress(context, AiRunStage.FileDepth, $"Reviewing {request.Path}...");

                var placeholders = new Dictionary<string, string>
                {
                    ["rules"] = EffectiveRules(),
                    ["adhoc_instructions"] = context.AdHocInstructions ?? "(none)",
                    ["path"] = request.Path,
                    ["before_oid"] = request.BeforeBlobOid ?? "(new file)",
                    ["after_oid"] = request.AfterBlobOid ?? "(deleted)",
                };
                var prompt = request.IncludeAnnotations
                    ? prompts.GetFileSummaryPrompt(placeholders)
                    : prompts.GetFileSummaryPrompt(placeholders);

                context.TurnsUsed++;
                await SendTurnWithBudgetAsync(context, prompt, workCt).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "File-depth review failed for {Path} on PR {PrNodeId}.", request.Path, request.PrNodeId);
                completion.TrySetException(ex);
            }
            finally
            {
                context.CurrentFileRequest = null;
            }
        }));

        await completion.Task.ConfigureAwait(false);
    }

    public Task<string> AskAsync(AiQuestionRequest request, CancellationToken ct = default) =>
        RunTurnForTextAsync(request.PrNodeId, BuildExplanationPrompt(request), AiWorkPriority.ExplicitUser, ct);

    private const int MaxChatHistoryMessages = 20;

    private string BuildExplanationPrompt(
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

    public Task<string> RunInlineActionAsync(AiInlineActionRequest request, CancellationToken ct = default)
    {
        var placeholders = new Dictionary<string, string>
        {
            ["context"] = $"File: {request.Path}",
            ["action"] = request.Action,
            ["selection"] = request.SelectedLinesContext,
        };
        return RunTurnForTextAsync(request.PrNodeId, prompts.GetCommentSuggestionPrompt(placeholders), AiWorkPriority.ExplicitUser, ct);
    }

    public async Task<string> ChatAsync(AiQuestionRequest request, CancellationToken ct = default)
    {
        var prior = await resultStore.ListChatMessagesAsync(request.PrNodeId, ct).ConfigureAwait(false);
        IReadOnlyList<AiChatMessage> cappedPrior = prior.Count <= MaxChatHistoryMessages
            ? prior
            : prior.Skip(prior.Count - MaxChatHistoryMessages).ToList();

        await resultStore.AppendChatMessageAsync(
            request.PrNodeId, new AiChatMessage("user", request.Question, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        var answer = await RunTurnForTextAsync(
                request.PrNodeId,
                BuildExplanationPrompt(request, cappedPrior),
                AiWorkPriority.ExplicitUser,
                ct)
            .ConfigureAwait(false);

        await resultStore.AppendChatMessageAsync(
            request.PrNodeId, new AiChatMessage("assistant", answer, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        return answer;
    }

    public async Task ClearAiDataAsync(CancellationToken ct = default)
    {
        await resultStore.ClearAllAsync(ct).ConfigureAwait(false);
        await materialiser.ClearAllExportsAsync(ct).ConfigureAwait(false);
        _runsByPr.Clear();
        _annotationsByBlob.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _runsByPr.Values)
        {
            if (context.Session is not null)
                await context.Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------
    // Run implementation.
    // ---------------------------------------------------------------------

    private async Task<AiRunSnapshot> RunTriageAsync(AiReviewRequest request, RunContext context, CancellationToken ct)
    {
        AgentPermissionPolicy? policy = null;
        try
        {
            var capture = new TriageCapture();
            policy = await OpenOrResumeSessionAsync(
                    context,
                    resumeSessionId: context.CopilotSessionId,
                    capture,
                    ct)
                .ConfigureAwait(false);

            NotifyProgress(context, AiRunStage.Triaging, "Reviewing changed files...");
            var placeholders = new Dictionary<string, string>
            {
                ["rules"] = EffectiveRules(),
                ["facts"] = factsAssembler.BuildFactsBlock(request),
                ["adhoc_instructions"] = request.AdHocInstructions ?? "(none)",
            };
            var prompt = prompts.GetTriagePrompt(placeholders);

            context.TurnsUsed++;
            await SendTurnWithBudgetAsync(context, prompt, ct).ConfigureAwait(false);

            if (capture.Result is null)
                return await FinishRunAsync(context, AiRunState.Incomplete, "The agent did not submit a triage result.", ct).ConfigureAwait(false);

            context.Triage = capture.Result;
            return await FinishRunAsync(context, AiRunState.Complete, errorMessage: null, ct).ConfigureAwait(false);
        }
        finally
        {
            LogPermissionDenials(context, policy);
        }
    }

    /// <summary>
    /// Materialises the review tree and creates or resumes a Copilot session on <paramref name="context"/>.
    /// </summary>
    private async Task<AgentPermissionPolicy> OpenOrResumeSessionAsync(
        RunContext context,
        string? resumeSessionId,
        TriageCapture capture,
        CancellationToken ct)
    {
        NotifyProgress(context, AiRunStage.Materialising, "Preparing a read-only working copy...");
        var materialised = await materialiser.MaterialiseAsync(context.RepositoryPath, context.HeadSha, ct)
            .ConfigureAwait(false);
        context.MaterialisedPath = materialised.Path;

        NotifyProgress(context, AiRunStage.Connecting, "Connecting to GitHub Copilot...");
        var token = await ResolveTokenAsync(context.RepositoryPath, ct).ConfigureAwait(false);

        var tools = BuildTools(context, capture);
        var policy = new AgentPermissionPolicy(settingsStore.Current.AiPathDenylist);

        var options = new AgentSessionOptions(
            Cwd: materialised.Path,
            GitHubToken: token,
            Model: context.Model,
            ReasoningEffort: settingsStore.Current.AiReasoningEffort,
            Tools: tools,
            OnPermissionRequest: policy.Evaluate,
            Streaming: true);

        var session = resumeSessionId is { Length: > 0 } existingSessionId
            ? await agentClient.ResumeSessionAsync(existingSessionId, options, ct).ConfigureAwait(false)
            : await agentClient.CreateSessionAsync(options, ct).ConfigureAwait(false);

        context.Session = session;
        context.CopilotSessionId = session.SessionId;
        return policy;
    }

    private async Task SendTurnWithBudgetAsync(RunContext context, string prompt, CancellationToken ct)
    {
        if (context.Session is null)
            throw new InvalidOperationException("The agent session has not been started.");

        var budget = settingsStore.Current.AiTurnBudget;
        if (budget > 0 && context.TurnsUsed > budget)
            throw new InvalidOperationException("The AI turn budget for this run has been reached.");

        var turnTimeoutSeconds = Math.Max(10, settingsStore.Current.AiTurnTimeoutSeconds);
        var waitTimeout = TimeSpan.FromSeconds(turnTimeoutSeconds);

        logger.LogInformation(
            "AI turn starting: runId={RunId} sessionId={SessionId} turnsUsed={TurnsUsed} budget={Budget} turnTimeoutSeconds={TurnTimeout} runTimeoutSeconds={RunTimeout}",
            context.RunId,
            context.CopilotSessionId,
            context.TurnsUsed,
            budget,
            turnTimeoutSeconds,
            context.RunTimeoutSeconds);

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(waitTimeout);
        await context.Session.SendTurnAsync(prompt, waitTimeout, turnCts.Token).ConfigureAwait(false);
    }

    private void LogPermissionDenials(RunContext context, AgentPermissionPolicy? policy)
    {
        if (policy is null || policy.Denials.Count == 0)
            return;

        foreach (var denial in policy.Denials)
        {
            logger.LogInformation(
                "AI permission denied during run {RunId}: kind={Kind} path={Path} tool={Tool} command={Command}",
                context.RunId,
                denial.Kind,
                denial.Path,
                denial.ToolName,
                denial.Command);
        }
    }

    private async Task<AiRunSnapshot> FinishRunAsync(RunContext context, AiRunState state, string? errorMessage, CancellationToken ct)
    {
        context.State = state;
        context.ErrorMessage = errorMessage;
        context.FinishedUtc = DateTimeOffset.UtcNow;

        await resultStore.UpsertRunAsync(new AiRunRecord(
            context.RunId!, context.PrNodeId, context.HeadSha, context.MergeBaseSha, context.CopilotSessionId,
            state, context.TurnsUsed, context.AdHocInstructions, context.CacheKey ?? "", errorMessage,
            context.StartedUtc, context.FinishedUtc), ct).ConfigureAwait(false);

        NotifyProgress(context, AiRunStage.Done, errorMessage);

        return new AiRunSnapshot(
            context.RunId!, context.PrNodeId, context.HeadSha, context.MergeBaseSha, state, context.CopilotSessionId,
            context.TurnsUsed, context.AdHocInstructions, context.Triage, errorMessage, context.StartedUtc, context.FinishedUtc);
    }

    private async Task<string> RunTurnForTextAsync(string prNodeId, string prompt, AiWorkPriority priority, CancellationToken ct)
    {
        var context = await EnsureLiveSessionAsync(prNodeId, ct).ConfigureAwait(false);
        var gateReason = CheckGate(context.RepositoryKey);
        if (gateReason is not null)
            throw new InvalidOperationException(gateReason);

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new StringBuilder();
        void OnDelta(string delta) => buffer.Append(delta);

        workQueue.Enqueue(new AiWorkItem(context.RepositoryKey, priority, Path: null, async workCt =>
        {
            context.Session!.AssistantDelta += OnDelta;
            try
            {
                context.TurnsUsed++;
                await SendTurnWithBudgetAsync(context, prompt, workCt).ConfigureAwait(false);
                completion.TrySetResult(buffer.ToString().Trim());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                context.Session!.AssistantDelta -= OnDelta;
            }
        }));

        return await completion.Task.ConfigureAwait(false);
    }

    private async Task<RunContext> EnsureLiveSessionAsync(string prNodeId, CancellationToken ct)
    {
        if (!_runsByPr.TryGetValue(prNodeId, out var context))
        {
            throw new InvalidOperationException(
                $"No AI review context for PR '{prNodeId}'. Open the pull request after a completed AI review, or start a review first.");
        }

        if (context.Session is not null)
            return context;

        if (string.IsNullOrEmpty(context.CopilotSessionId))
        {
            throw new InvalidOperationException(
                $"The cached AI review for PR '{prNodeId}' has no resumable Copilot session. Start a new review first.");
        }

        await context.SessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (context.Session is not null)
                return context;

            try
            {
                await OpenOrResumeSessionAsync(
                        context,
                        resumeSessionId: context.CopilotSessionId,
                        new TriageCapture(),
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Failed to resume the Copilot session for PR '{prNodeId}'. The session may have expired. Start a new review. ({ex.Message})",
                    ex);
            }

            return context;
        }
        finally
        {
            context.SessionGate.Release();
        }
    }

    private RunContext RegisterRunContext(
        AiReviewRequest request,
        string runId,
        string? copilotSessionId,
        AiPrTriageResult? triage,
        AiRunState state)
    {
        var context = _runsByPr.GetOrAdd(request.PrNodeId, static _ => new RunContext());
        context.Request = request;
        context.PrNodeId = request.PrNodeId;
        context.RepositoryKey = request.RepositoryKey;
        context.RepositoryPath = request.RepositoryPath;
        context.HeadSha = request.HeadSha;
        context.MergeBaseSha = request.MergeBaseSha;
        context.AdHocInstructions = request.AdHocInstructions;
        context.RunId = runId;
        context.CopilotSessionId = copilotSessionId;
        context.Triage = triage;
        context.State = state;
        context.Model = settingsStore.Current.AiModelOverride;
        context.RulesHash = AiCacheKeys.Hash(EffectiveRules());
        context.InstructionsHash = AiCacheKeys.Hash(request.AdHocInstructions);
        return context;
    }

    private void NotifyProgress(RunContext context, AiRunStage stage, string? message)
    {
        if (!_observersByRepo.TryGetValue(context.RepositoryKey, out var observers))
            return;

        var elapsed = DateTimeOffset.UtcNow - context.StartedUtc;
        observers.Notify(new AiRunProgress(
            stage, context.TurnsUsed, settingsStore.Current.AiTurnBudget, FilesCompleted: 0, FilesTotal: 0, elapsed, message));
    }

    private string? CheckGate(string repositoryKey)
    {
        var settings = settingsStore.Current;
        if (!settings.AiAssistanceEnabled)
            return "AI assistance is disabled in settings.";

        if (!settings.AiDisclosureAcknowledged)
            return "AI assistance requires acknowledging the data-sharing disclosure in settings.";

        if (settings.AiExcludedRepositories.Contains(repositoryKey, StringComparer.OrdinalIgnoreCase))
            return "AI assistance is disabled for this repository.";

        return null;
    }

    private string EffectiveRules()
    {
        var rules = settingsStore.Current.AiReviewRules;
        return string.IsNullOrWhiteSpace(rules) ? prompts.GetDefaultReviewRules() : rules;
    }

    private async Task<string?> ResolveTokenAsync(string repositoryPath, CancellationToken ct)
    {
        var settings = settingsStore.Current;
        if (settings.AiUseDedicatedCopilotToken)
            return await tokenStore.GetTokenAsync(DedicatedTokenHost, DedicatedTokenLogin, ct).ConfigureAwait(false);

        var binding = settings.RepositoryBindings.FirstOrDefault(b =>
            string.Equals(b.LocalPath, repositoryPath, StringComparison.OrdinalIgnoreCase));
        if (binding is null)
            return null;

        var account = settings.Accounts.FirstOrDefault(a =>
            string.Equals(a.Host, binding.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Login, binding.AccountLogin, StringComparison.OrdinalIgnoreCase));
        if (account is null)
            return null;

        return await tokenStore.GetTokenAsync(account.Host, account.Login, ct).ConfigureAwait(false);
    }

    private static AiRunSnapshot FailedSnapshot(AiReviewRequest request, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiRunSnapshot(
            Guid.NewGuid().ToString("N"), request.PrNodeId, request.HeadSha, request.MergeBaseSha, AiRunState.Failed,
            CopilotSessionId: null, TurnsUsed: 0, request.AdHocInstructions, Triage: null, reason, now, now);
    }

    private static AiRunSnapshot ToSnapshot(AiRunRecord run, AiPrTriageResult? triage) => new(
        run.Id, run.PrNodeId, run.HeadSha, run.MergeBaseSha, run.State, run.CopilotSessionId,
        run.TurnsUsed, run.AdHocInstructions, triage, run.ErrorMessage, run.StartedUtc, run.FinishedUtc);

    private static AIChecklistSeverity MapSeverity(AiRiskLevel risk) => risk switch
    {
        AiRiskLevel.Low => AIChecklistSeverity.Info,
        AiRiskLevel.Medium => AIChecklistSeverity.Suggestion,
        AiRiskLevel.High => AIChecklistSeverity.Warning,
        AiRiskLevel.Critical => AIChecklistSeverity.Risk,
        _ => AIChecklistSeverity.Info,
    };

    private static IDiffAnnotation ToDiffAnnotation(AiAnnotationResult result)
    {
        var content = new ContentId(result.BlobOid);
        var range = new AnnotationRange(
            new DiffAnchor(result.Side, content, result.StartLine),
            new DiffAnchor(result.Side, content, result.EndLine));
        return new AiDiffAnnotation(range, result);
    }

    private static AiAnnotationResult ToAnnotationResult(AiAnnotationRecord record) => new(
        record.Id, record.Path, record.BlobOid, record.StartLine, record.EndLine,
        Enum.Parse<DiffSide>(record.Side), Enum.Parse<AiAnnotationSeverity>(record.Severity), record.Body, record.ReadState);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    // ---------------------------------------------------------------------
    // Custom tools invoked by the agent.
    // ---------------------------------------------------------------------

    private IReadOnlyList<AgentCustomTool> BuildTools(RunContext context, TriageCapture capture) =>
    [
        new AgentCustomTool(
            "submit_pr_triage",
            "Submit the final pull request triage result. Call exactly once, at the end of the review.",
            (argsJson, ct) => HandleSubmitPrTriage(context, capture, argsJson, ct)),
        new AgentCustomTool(
            "submit_file_summary",
            "Submit the summary for the file currently being reviewed in depth.",
            (argsJson, ct) => HandleSubmitFileSummary(context, argsJson, ct)),
        new AgentCustomTool(
            "add_annotation",
            "Add an inline review annotation at a specific location in the file currently being reviewed.",
            (argsJson, ct) => HandleAddAnnotation(context, argsJson, ct)),
    ];

    private async Task<string> HandleSubmitPrTriage(RunContext context, TriageCapture capture, string argsJson, CancellationToken ct)
    {
        try
        {
            var triage = Deserialize<AiPrTriageResult>(argsJson) ?? throw new InvalidOperationException("Empty triage payload.");
            triage = triage with { Measured = factsAssembler.ComputeMeasuredFacts(context.Request) };
            capture.Result = triage;

            await resultStore.UpsertPrResultAsync(new AiPrResultRecord(
                context.RunId!, context.PrNodeId, context.CacheKey ?? "", Serialize(triage), DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);

            foreach (var file in triage.Files)
            {
                var fact = context.Request.ChangedFiles.FirstOrDefault(f => string.Equals(f.Path, file.Path, StringComparison.Ordinal));
                var fileCacheKey = AiCacheKeys.ComputeFileKey(
                    file.Path, fact?.BeforeBlobOid, fact?.AfterBlobOid, AiPromptCatalog.PromptVersion,
                    context.Model, context.RulesHash ?? "", context.InstructionsHash ?? "");

                await resultStore.UpsertFileResultAsync(new AiFileResultRecord(
                    context.RunId!, context.PrNodeId, file.Path, fileCacheKey, file.Classification.ToString(),
                    file.PriorityStars, file.Guidance, SummaryJson: null, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            }

            return """{"status":"ok"}""";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process submit_pr_triage payload for PR {PrNodeId}.", context.PrNodeId);
            return Serialize(new ToolErrorResult(ex.Message));
        }
    }

    private async Task<string> HandleSubmitFileSummary(RunContext context, string argsJson, CancellationToken ct)
    {
        try
        {
            var summary = Deserialize<AiFileSummaryResult>(argsJson) ?? throw new InvalidOperationException("Empty file summary payload.");
            var fileContext = context.CurrentFileRequest
                ?? throw new InvalidOperationException("No file-depth request is currently in progress.");

            var cacheKey = AiCacheKeys.ComputeFileKey(
                fileContext.Path, fileContext.BeforeOid, fileContext.AfterOid, AiPromptCatalog.PromptVersion,
                context.Model, context.RulesHash ?? "", context.InstructionsHash ?? "");

            var existing = await resultStore.GetFileResultByCacheKeyAsync(cacheKey, ct).ConfigureAwait(false);
            var record = new AiFileResultRecord(
                existing?.RunId ?? context.RunId ?? "",
                context.PrNodeId,
                summary.Path,
                cacheKey,
                existing?.Classification,
                existing?.PriorityStars ?? 0,
                existing?.Guidance,
                Serialize(summary),
                DateTimeOffset.UtcNow);

            await resultStore.UpsertFileResultAsync(record, ct).ConfigureAwait(false);
            return """{"status":"ok"}""";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process submit_file_summary payload for PR {PrNodeId}.", context.PrNodeId);
            return Serialize(new ToolErrorResult(ex.Message));
        }
    }

    private async Task<string> HandleAddAnnotation(RunContext context, string argsJson, CancellationToken ct)
    {
        try
        {
            var args = Deserialize<AddAnnotationArgs>(argsJson) ?? throw new InvalidOperationException("Empty annotation payload.");
            var id = Guid.NewGuid().ToString("N");

            await resultStore.UpsertAnnotationAsync(new AiAnnotationRecord(
                id, context.RunId ?? "", context.PrNodeId, args.Path, args.BlobOid, args.StartLine, args.EndLine,
                args.Side.ToString(), args.Severity.ToString(), args.Body, AiAnnotationReadState.Unread, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);

            var result = new AiAnnotationResult(
                id, args.Path, args.BlobOid, args.StartLine, args.EndLine, args.Side, args.Severity, args.Body, AiAnnotationReadState.Unread);

            var list = _annotationsByBlob.GetOrAdd(args.BlobOid, static _ => []);
            lock (list)
                list.Add(result);

            return """{"status":"ok"}""";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process add_annotation payload for PR {PrNodeId}.", context.PrNodeId);
            return Serialize(new ToolErrorResult(ex.Message));
        }
    }

    private sealed record ToolErrorResult(string Message)
    {
        public string Status => "error";
    }

    private sealed record AddAnnotationArgs(
        string Path,
        string BlobOid,
        int StartLine,
        int EndLine,
        DiffSide Side,
        AiAnnotationSeverity Severity,
        string Body);

    private sealed class TriageCapture
    {
        public AiPrTriageResult? Result;
    }

    private sealed record FileDepthContext(string Path, string? BeforeOid, string? AfterOid);

    private sealed class RunContext
    {
        public AiReviewRequest Request = null!;
        public string PrNodeId = "";
        public string RepositoryKey = "";
        public string RepositoryPath = "";
        public string HeadSha = "";
        public string MergeBaseSha = "";
        public string? RunId;
        public string? CacheKey;
        public string? CopilotSessionId;
        public IAgentSession? Session;
        public readonly SemaphoreSlim SessionGate = new(1, 1);
        public string? MaterialisedPath;
        public string? Model;
        public string? RulesHash;
        public string? InstructionsHash;
        public string? AdHocInstructions;
        public int TurnsUsed;
        public int RunTimeoutSeconds;
        public bool UserCancelled;
        public AiRunState State = AiRunState.Idle;
        public AiPrTriageResult? Triage;
        public string? ErrorMessage;
        public DateTimeOffset StartedUtc;
        public DateTimeOffset? FinishedUtc;
        public CancellationTokenSource? RunCts;
        public FileDepthContext? CurrentFileRequest;
    }

    private sealed class RepoObservers
    {
        private readonly Lock _lock = new();
        private readonly List<Action<AiRunProgress>> _handlers = [];

        public IDisposable Add(Action<AiRunProgress> handler)
        {
            lock (_lock)
                _handlers.Add(handler);

            return new Subscription(this, handler);
        }

        public void Notify(AiRunProgress progress)
        {
            Action<AiRunProgress>[] snapshot;
            lock (_lock)
                snapshot = [.. _handlers];

            foreach (var handler in snapshot)
                handler(progress);
        }

        private void Remove(Action<AiRunProgress> handler)
        {
            lock (_lock)
                _handlers.Remove(handler);
        }

        private sealed class Subscription(RepoObservers owner, Action<AiRunProgress> handler) : IDisposable
        {
            public void Dispose() => owner.Remove(handler);
        }
    }
}

/// <summary>Simple <see cref="IDiffAnnotation"/> implementation backing AI-sourced overlays.</summary>
internal sealed record AiDiffAnnotation(AnnotationRange Range, AiAnnotationResult Source) : IDiffAnnotation;

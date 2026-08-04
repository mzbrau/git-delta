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
    WorkingCopyMaterialiser workingCopyMaterialiser,
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

    private readonly ConcurrentDictionary<string, RunContext> _runsBySession = new();
    private readonly ConcurrentDictionary<string, RepoObservers> _observersByRepo = new();
    private readonly ConcurrentDictionary<string, List<AiAnnotationResult>> _annotationsByBlob = new();

    // ---------------------------------------------------------------------
    // Cache-only reads (never touch the agent).
    // ---------------------------------------------------------------------

    public async ValueTask<AiRunSnapshot?> GetCachedRunAsync(string sessionKey, CancellationToken ct = default)
    {
        var run = await resultStore.GetLatestRunAsync(sessionKey, ct).ConfigureAwait(false);
        if (run is null)
            return null;

        var prResult = await resultStore.GetPrResultForRunAsync(run.Id, ct).ConfigureAwait(false);
        var triage = prResult is null ? null : Deserialize<AiPrTriageResult>(prResult.PayloadJson);
        return ToSnapshot(run, triage);
    }

    public async Task AttachCachedRunAsync(AiReviewRequest request, CancellationToken ct = default)
    {
        // Keep an already-live session; attach is only for hydrating after process restart.
        if (_runsBySession.TryGetValue(request.SessionKey, out var existing) && existing.Session is not null)
            return;

        var run = await resultStore.GetLatestRunAsync(request.SessionKey, ct).ConfigureAwait(false);
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

    public ValueTask<IReadOnlyList<FilePath>> SuggestFileOrderAsync(
        string sessionKey,
        IReadOnlyList<FilePath> changedFiles,
        CancellationToken ct = default) =>
        ValueTask.FromResult(changedFiles);

    public ValueTask<IReadOnlyList<AIChecklistItem>> GetChecklistAsync(string sessionKey, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<AIChecklistItem>>([]);

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

    public async ValueTask<AiFileSummaryResult?> GetFileSummaryAsync(string sessionKey, string path, CancellationToken ct = default)
    {
        var run = await resultStore.GetLatestRunAsync(sessionKey, ct).ConfigureAwait(false);
        if (run is null)
            return null;

        var files = await resultStore.ListFileResultsForRunAsync(run.Id, ct).ConfigureAwait(false);
        var match = files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));
        return match?.SummaryJson is null ? null : Deserialize<AiFileSummaryResult>(match.SummaryJson);
    }

    public async ValueTask<IReadOnlyList<AiAnnotationResult>> GetFileAnnotationsAsync(
        string sessionKey,
        string path,
        bool includeDismissed = false,
        CancellationToken ct = default)
    {
        var records = await resultStore.ListAnnotationsAsync(sessionKey, path, includeDismissed, ct).ConfigureAwait(false);
        return [.. records.Select(ToAnnotationResult)];
    }

    public Task SetAnnotationReadStateAsync(string annotationId, AiAnnotationReadState state, CancellationToken ct = default) =>
        resultStore.SetAnnotationReadStateAsync(annotationId, state, ct);

    public async ValueTask<IReadOnlyList<AiChatMessage>> GetChatHistoryAsync(string sessionKey, CancellationToken ct = default) =>
        await resultStore.ListChatMessagesAsync(sessionKey, ct).ConfigureAwait(false);

    public Task ClearChatHistoryAsync(string sessionKey, CancellationToken ct = default) =>
        resultStore.ClearChatMessagesAsync(sessionKey, ct);

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

        // Working-copy reviews key the cache on the current staged/all snapshot tree OID.
        if (IsWorkingCopyScope(request.Scope))
        {
            var treeOid = await workingCopyMaterialiser
                .WriteTreeAsync(request.RepositoryPath, request.Scope, ct)
                .ConfigureAwait(false);
            request = request with { HeadSha = treeOid };
        }

        var rulesHash = AiCacheKeys.Hash(EffectiveRules());
        var instructionsHash = AiCacheKeys.Hash(request.AdHocInstructions);
        var model = settingsStore.Current.AiModelOverride;
        var cacheKey = AiCacheKeys.ComputePrTriageKey(
            request.SessionKey, request.HeadSha, request.MergeBaseSha, request.Scope.ToString(),
            AiPromptCatalog.PromptVersion, model, rulesHash, instructionsHash);

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
            else
            {
                // Triage no longer writes a PR payload; reuse a complete shell run with the same cache key.
                var latestRun = await resultStore.GetLatestRunAsync(request.SessionKey, ct).ConfigureAwait(false);
                if (latestRun is { State: AiRunState.Complete } &&
                    string.Equals(latestRun.CacheKey, cacheKey, StringComparison.Ordinal))
                {
                    var context = RegisterRunContext(
                        request, latestRun.Id, latestRun.CopilotSessionId, triage: null, AiRunState.Complete);
                    context.CacheKey = cacheKey;
                    return ToSnapshot(latestRun, triage: null);
                }
            }
        }

        string? resumeSessionId = null;
        if (request.Resume)
        {
            var previousRun = await resultStore.GetLatestRunAsync(request.SessionKey, ct).ConfigureAwait(false);
            resumeSessionId = previousRun?.CopilotSessionId;
        }

        var runId = Guid.NewGuid().ToString("N");
        var startedUtc = DateTimeOffset.UtcNow;
        var runContext = RegisterRunContext(request, runId, resumeSessionId, triage: null, AiRunState.Running);
        runContext.CacheKey = cacheKey;
        runContext.StartedUtc = startedUtc;
        runContext.UserCancelled = false;
        runContext.TurnIdleTimedOut = false;
        runContext.RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var configuredRunTimeout = settingsStore.Current.AiRunTimeoutSeconds;
        // 0 = unlimited; when enabled, enforce a small floor so tiny values are not accidental.
        var runTimeoutSeconds = configuredRunTimeout <= 0 ? 0 : Math.Max(30, configuredRunTimeout);
        runContext.RunTimeoutSeconds = runTimeoutSeconds;
        if (runTimeoutSeconds > 0)
            runContext.RunCts.CancelAfter(TimeSpan.FromSeconds(runTimeoutSeconds));

        await resultStore.UpsertRunAsync(new AiRunRecord(
            runId, request.SessionKey, request.HeadSha, request.MergeBaseSha, CopilotSessionId: resumeSessionId,
            AiRunState.Running, TurnsUsed: 0, request.AdHocInstructions, cacheKey, ErrorMessage: null,
            startedUtc, FinishedUtc: null), ct).ConfigureAwait(false);

        // Triage was removed; open a durable run shell so file-depth / chat can attach later.
        runContext.Triage = null;
        NotifyActivityLog(runContext, "AI review session ready (triage disabled; file-depth available).");
        return await FinishRunAsync(runContext, AiRunState.Complete, errorMessage: null, ct).ConfigureAwait(false);
    }

    public Task CancelAsync(string repositoryKey, CancellationToken ct = default)
    {
        workQueue.CancelRepository(repositoryKey);
        foreach (var context in _runsBySession.Values)
        {
            if (!string.Equals(context.RepositoryKey, repositoryKey, StringComparison.Ordinal))
                continue;

            context.UserCancelled = true;
            context.RunCts?.Cancel();
        }

        return Task.CompletedTask;
    }

    public IDisposable ObserveProgress(string repositoryKey, Action<AiRunProgress> handler) =>
        _observersByRepo.GetOrAdd(repositoryKey, static _ => new RepoObservers()).AddProgress(handler);

    public IDisposable ObserveActivityLog(string repositoryKey, Action<string> handler) =>
        _observersByRepo.GetOrAdd(repositoryKey, static _ => new RepoObservers()).AddActivityLog(handler);

    public async Task RequestFileDepthAsync(AiFileDepthRequest request, CancellationToken ct = default)
    {
        var context = await EnsureLiveSessionAsync(request.SessionKey, ct).ConfigureAwait(false);
        var gateReason = CheckGate(context.RepositoryKey);
        if (gateReason is not null)
            throw new InvalidOperationException(gateReason);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workQueue.Enqueue(new AiWorkItem(context.RepositoryKey, AiWorkPriority.OpenFile, request.Path, async workCt =>
        {
            context.CurrentFileRequest = new FileDepthContext(request.Path, request.BeforeBlobOid, request.AfterBlobOid);
            try
            {
                NotifyProgress(context, AiRunStage.FileDepth, $"Waiting on: reviewing {request.Path}");
                NotifyActivityLog(context, $"Waiting on: reviewing {request.Path}");

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
                logger.LogWarning(ex, "File-depth review failed for {Path} on session {SessionKey}.", request.Path, request.SessionKey);
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
        RunTurnForTextAsync(request.SessionKey, BuildExplanationPrompt(request), AiWorkPriority.ExplicitUser, ct);

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
        return RunTurnForTextAsync(request.SessionKey, prompts.GetCommentSuggestionPrompt(placeholders), AiWorkPriority.ExplicitUser, ct);
    }

    public async Task<string> ChatAsync(AiQuestionRequest request, CancellationToken ct = default)
    {
        var prior = await resultStore.ListChatMessagesAsync(request.SessionKey, ct).ConfigureAwait(false);
        IReadOnlyList<AiChatMessage> cappedPrior = prior.Count <= MaxChatHistoryMessages
            ? prior
            : prior.Skip(prior.Count - MaxChatHistoryMessages).ToList();

        await resultStore.AppendChatMessageAsync(
            request.SessionKey, new AiChatMessage("user", request.Question, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        var answer = await RunTurnForTextAsync(
                request.SessionKey,
                BuildExplanationPrompt(request, cappedPrior),
                AiWorkPriority.ExplicitUser,
                ct)
            .ConfigureAwait(false);

        await resultStore.AppendChatMessageAsync(
            request.SessionKey, new AiChatMessage("assistant", answer, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        return answer;
    }

    public async Task ClearAiDataAsync(CancellationToken ct = default)
    {
        await resultStore.ClearAllAsync(ct).ConfigureAwait(false);
        await materialiser.ClearAllExportsAsync(ct).ConfigureAwait(false);
        _runsBySession.Clear();
        _annotationsByBlob.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _runsBySession.Values)
        {
            if (context.Session is not null)
                await context.Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------
    // Run implementation.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Materialises the review tree and creates or resumes a Copilot session on <paramref name="context"/>.
    /// </summary>
    private async Task<AgentPermissionPolicy> OpenOrResumeSessionAsync(
        RunContext context,
        string? resumeSessionId,
        CancellationToken ct)
    {
        NotifyProgress(context, AiRunStage.Materialising, "Waiting on: materialising working copy");
        NotifyActivityLog(context, "Waiting on: materialising working copy");
        var materialised = IsWorkingCopyScope(context.Request.Scope)
            ? await workingCopyMaterialiser
                .MaterialiseAsync(context.RepositoryPath, context.HeadSha, ct)
                .ConfigureAwait(false)
            : await materialiser.MaterialiseAsync(context.RepositoryPath, context.HeadSha, ct)
                .ConfigureAwait(false);
        context.MaterialisedPath = materialised.Path;

        NotifyProgress(context, AiRunStage.Connecting, "Waiting on: connecting to GitHub Copilot");
        NotifyActivityLog(context, "Waiting on: connecting to GitHub Copilot");
        var token = await ResolveTokenAsync(context.RepositoryPath, ct).ConfigureAwait(false);

        var tools = BuildTools(context);
        var policy = new AgentPermissionPolicy(settingsStore.Current.AiPathDenylist);

        AgentPermissionDecision OnPermission(AgentPermissionRequest request)
        {
            context.ReportAgentActivity?.Invoke();
            return policy.Evaluate(request);
        }

        var options = new AgentSessionOptions(
            Cwd: materialised.Path,
            GitHubToken: token,
            Model: context.Model,
            ReasoningEffort: settingsStore.Current.AiReasoningEffort,
            Tools: tools,
            OnPermissionRequest: OnPermission,
            Streaming: true);

        var session = resumeSessionId is { Length: > 0 } existingSessionId
            ? await agentClient.ResumeSessionAsync(existingSessionId, options, ct).ConfigureAwait(false)
            : await agentClient.CreateSessionAsync(options, ct).ConfigureAwait(false);

        context.Session = session;
        context.CopilotSessionId = session.SessionId;
        return policy;
    }

    /// <summary>
    /// SDK treats null timeout as 60s; idle watchdog owns cancellation, so pass a large ceiling.
    /// </summary>
    private static readonly TimeSpan SdkTurnWaitCeiling = TimeSpan.FromDays(1);

    private async Task SendTurnWithBudgetAsync(RunContext context, string prompt, CancellationToken ct)
    {
        if (context.Session is null)
            throw new InvalidOperationException("The agent session has not been started.");

        var budget = settingsStore.Current.AiTurnBudget;
        if (budget > 0 && context.TurnsUsed > budget)
            throw new InvalidOperationException("The AI turn budget for this run has been reached.");

        var turnTimeoutSeconds = Math.Max(10, settingsStore.Current.AiTurnTimeoutSeconds);
        var idleTimeout = TimeSpan.FromSeconds(turnTimeoutSeconds);
        context.TurnIdleTimedOut = false;

        logger.LogInformation(
            "AI turn starting: runId={RunId} sessionId={SessionId} turnsUsed={TurnsUsed} budget={Budget} turnIdleTimeoutSeconds={TurnTimeout} runTimeoutSeconds={RunTimeout}",
            context.RunId,
            context.CopilotSessionId,
            context.TurnsUsed,
            budget,
            turnTimeoutSeconds,
            context.RunTimeoutSeconds);

        NotifyActivityLog(context, $"--- Turn {context.TurnsUsed} starting (idle timeout {turnTimeoutSeconds}s) ---");
        NotifyActivityLog(context, ">>> Prompt");
        NotifyActivityLog(context, prompt);
        NotifyActivityLog(context, "<<< End prompt");
        NotifyProgress(context, GuessStage(context), "Waiting on: Copilot response");

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(idleTimeout);

        var assistantBuffer = new StringBuilder();
        var session = context.Session;
        var toolsInFlight = 0;

        void ResetIdle()
        {
            try
            {
                if (!turnCts.IsCancellationRequested && Volatile.Read(ref toolsInFlight) == 0)
                    turnCts.CancelAfter(idleTimeout);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void PauseIdleForTool()
        {
            Interlocked.Increment(ref toolsInFlight);
            try
            {
                if (!turnCts.IsCancellationRequested)
                    turnCts.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void ResumeIdleAfterTool()
        {
            if (Interlocked.Decrement(ref toolsInFlight) == 0)
                ResetIdle();
        }

        void FlushAssistant()
        {
            if (assistantBuffer.Length == 0)
                return;

            NotifyActivityLog(context, ">>> Assistant");
            NotifyActivityLog(context, assistantBuffer.ToString());
            NotifyActivityLog(context, "<<< End assistant");
            assistantBuffer.Clear();
        }

        void OnDelta(string delta)
        {
            ResetIdle();
            assistantBuffer.Append(delta);
            if (assistantBuffer.Length >= 256 || delta.Contains('\n'))
                FlushAssistant();
        }

        void OnToolStarted(string name, string argsJson)
        {
            PauseIdleForTool();
            FlushAssistant();
            NotifyActivityLog(context, $">>> Tool start: {name}");
            NotifyActivityLog(context, argsJson);
            NotifyProgress(context, GuessStage(context), $"Waiting on: tool {name}");
        }

        void OnToolCompleted(AgentToolCall call)
        {
            NotifyActivityLog(context, $"<<< Tool result: {call.Name}");
            if (!string.IsNullOrEmpty(call.ResultJson))
                NotifyActivityLog(context, call.ResultJson);
            ResumeIdleAfterTool();
            NotifyProgress(context, GuessStage(context), "Waiting on: Copilot response");
        }

        void OnExternalActivity() => ResetIdle();

        context.ReportAgentActivity = OnExternalActivity;
        session.AssistantDelta += OnDelta;
        session.ToolActivityStarted += OnToolStarted;
        session.ToolCallReceived += OnToolCompleted;
        try
        {
            await session.SendTurnAsync(prompt, SdkTurnWaitCeiling, turnCts.Token).ConfigureAwait(false);
            FlushAssistant();
            NotifyActivityLog(context, $"--- Turn {context.TurnsUsed} completed ---");
        }
        catch (OperationCanceledException) when (!context.UserCancelled && turnCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            context.TurnIdleTimedOut = true;
            FlushAssistant();
            NotifyActivityLog(context,
                $"Turn idle timeout after {turnTimeoutSeconds}s with no Copilot activity.");
            throw new TimeoutException(
                $"No Copilot activity for {turnTimeoutSeconds}s (turn idle timeout).");
        }
        finally
        {
            context.ReportAgentActivity = null;
            session.AssistantDelta -= OnDelta;
            session.ToolActivityStarted -= OnToolStarted;
            session.ToolCallReceived -= OnToolCompleted;
        }
    }

    private static AiRunStage GuessStage(RunContext context) =>
        context.CurrentFileRequest is not null ? AiRunStage.FileDepth : AiRunStage.Triaging;

    private string ClassifyCancellation(RunContext context)
    {
        if (context.UserCancelled)
            return "The review was cancelled.";

        if (context.TurnIdleTimedOut)
        {
            var turnTimeout = Math.Max(10, settingsStore.Current.AiTurnTimeoutSeconds);
            return $"AI review timed out after {turnTimeout}s with no Copilot activity (turn idle timeout).";
        }

        if (context.RunTimeoutSeconds > 0)
            return $"AI review timed out after {context.RunTimeoutSeconds}s (run timeout).";

        return "The review was cancelled.";
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
            context.RunId!, context.SessionKey, context.HeadSha, context.MergeBaseSha, context.CopilotSessionId,
            state, context.TurnsUsed, context.AdHocInstructions, context.CacheKey ?? "", errorMessage,
            context.StartedUtc, context.FinishedUtc), ct).ConfigureAwait(false);

        NotifyProgress(context, AiRunStage.Done, errorMessage);
        if (!string.IsNullOrWhiteSpace(errorMessage))
            NotifyActivityLog(context, errorMessage);
        else if (state == AiRunState.Complete)
            NotifyActivityLog(context, "Review completed.");

        return new AiRunSnapshot(
            context.RunId!, context.SessionKey, context.HeadSha, context.MergeBaseSha, state, context.CopilotSessionId,
            context.TurnsUsed, context.AdHocInstructions, context.Triage, errorMessage, context.StartedUtc, context.FinishedUtc);
    }

    private async Task<string> RunTurnForTextAsync(string sessionKey, string prompt, AiWorkPriority priority, CancellationToken ct)
    {
        var context = await EnsureLiveSessionAsync(sessionKey, ct).ConfigureAwait(false);
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

    private async Task<RunContext> EnsureLiveSessionAsync(string sessionKey, CancellationToken ct)
    {
        if (!_runsBySession.TryGetValue(sessionKey, out var context))
        {
            throw new InvalidOperationException(
                $"No AI review context for session '{sessionKey}'. Open the pull request after a completed AI review, or start a review first.");
        }

        if (context.Session is not null)
            return context;

        await context.SessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (context.Session is not null)
                return context;

            try
            {
                // StartReview no longer opens a Copilot session; create one lazily for file-depth / chat,
                // or resume when a prior CopilotSessionId was persisted.
                await OpenOrResumeSessionAsync(
                        context,
                        resumeSessionId: context.CopilotSessionId,
                        ct)
                    .ConfigureAwait(false);

                if (context.RunId is not null)
                {
                    await resultStore.UpsertRunAsync(new AiRunRecord(
                        context.RunId, context.SessionKey, context.HeadSha, context.MergeBaseSha,
                        context.CopilotSessionId, context.State, context.TurnsUsed, context.AdHocInstructions,
                        context.CacheKey ?? "", context.ErrorMessage, context.StartedUtc, context.FinishedUtc), ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var action = string.IsNullOrEmpty(context.CopilotSessionId) ? "open" : "resume";
                throw new InvalidOperationException(
                    $"Failed to {action} the Copilot session for '{sessionKey}'. Start a new review. ({ex.Message})",
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
        var context = _runsBySession.GetOrAdd(request.SessionKey, static _ => new RunContext());
        context.Request = request;
        context.SessionKey = request.SessionKey;
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
        observers.NotifyProgress(new AiRunProgress(
            stage, context.TurnsUsed, settingsStore.Current.AiTurnBudget, FilesCompleted: 0, FilesTotal: 0, elapsed, message));
    }

    private void NotifyActivityLog(RunContext context, string line)
    {
        if (!_observersByRepo.TryGetValue(context.RepositoryKey, out var observers))
            return;

        var stamped = $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {line}";
        observers.NotifyActivityLog(stamped);
    }

    private static bool IsWorkingCopyScope(AiReviewScope scope) =>
        scope is AiReviewScope.WorkingCopyStaged or AiReviewScope.WorkingCopyAll;

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
            Guid.NewGuid().ToString("N"), request.SessionKey, request.HeadSha, request.MergeBaseSha, AiRunState.Failed,
            CopilotSessionId: null, TurnsUsed: 0, request.AdHocInstructions, Triage: null, reason, now, now);
    }

    private static AiRunSnapshot ToSnapshot(AiRunRecord run, AiPrTriageResult? triage) => new(
        run.Id, run.SessionKey, run.HeadSha, run.MergeBaseSha, run.State, run.CopilotSessionId,
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

    private IReadOnlyList<AgentCustomTool> BuildTools(RunContext context) =>
    [
        new AgentCustomTool(
            "submit_file_summary",
            "Submit the summary for the file currently being reviewed in depth.",
            (argsJson, ct) => HandleSubmitFileSummary(context, argsJson, ct)),
        new AgentCustomTool(
            "add_annotation",
            "Add an inline review annotation at a specific location in the file currently being reviewed.",
            (argsJson, ct) => HandleAddAnnotation(context, argsJson, ct)),
    ];


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
                context.SessionKey,
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
            logger.LogWarning(ex, "Failed to process submit_file_summary payload for session {SessionKey}.", context.SessionKey);
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
                id, context.RunId ?? "", context.SessionKey, args.Path, args.BlobOid, args.StartLine, args.EndLine,
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
            logger.LogWarning(ex, "Failed to process add_annotation payload for session {SessionKey}.", context.SessionKey);
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

    private sealed record FileDepthContext(string Path, string? BeforeOid, string? AfterOid);

    private sealed class RunContext
    {
        public AiReviewRequest Request = null!;
        public string SessionKey = "";
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
        public bool TurnIdleTimedOut;
        public AiRunState State = AiRunState.Idle;
        public AiPrTriageResult? Triage;
        public string? ErrorMessage;
        public DateTimeOffset StartedUtc;
        public DateTimeOffset? FinishedUtc;
        public CancellationTokenSource? RunCts;
        public FileDepthContext? CurrentFileRequest;
        public Action? ReportAgentActivity;
    }

    private sealed class RepoObservers
    {
        private readonly Lock _lock = new();
        private readonly List<Action<AiRunProgress>> _progressHandlers = [];
        private readonly List<Action<string>> _activityLogHandlers = [];

        public IDisposable AddProgress(Action<AiRunProgress> handler)
        {
            lock (_lock)
                _progressHandlers.Add(handler);

            return new ProgressSubscription(this, handler);
        }

        public IDisposable AddActivityLog(Action<string> handler)
        {
            lock (_lock)
                _activityLogHandlers.Add(handler);

            return new ActivityLogSubscription(this, handler);
        }

        public void NotifyProgress(AiRunProgress progress)
        {
            Action<AiRunProgress>[] snapshot;
            lock (_lock)
                snapshot = [.. _progressHandlers];

            foreach (var handler in snapshot)
                handler(progress);
        }

        public void NotifyActivityLog(string line)
        {
            Action<string>[] snapshot;
            lock (_lock)
                snapshot = [.. _activityLogHandlers];

            foreach (var handler in snapshot)
                handler(line);
        }

        private void RemoveProgress(Action<AiRunProgress> handler)
        {
            lock (_lock)
                _progressHandlers.Remove(handler);
        }

        private void RemoveActivityLog(Action<string> handler)
        {
            lock (_lock)
                _activityLogHandlers.Remove(handler);
        }

        private sealed class ProgressSubscription(RepoObservers owner, Action<AiRunProgress> handler) : IDisposable
        {
            public void Dispose() => owner.RemoveProgress(handler);
        }

        private sealed class ActivityLogSubscription(RepoObservers owner, Action<string> handler) : IDisposable
        {
            public void Dispose() => owner.RemoveActivityLog(handler);
        }
    }
}

/// <summary>Simple <see cref="IDiffAnnotation"/> implementation backing AI-sourced overlays.</summary>
internal sealed record AiDiffAnnotation(AnnotationRange Range, AiAnnotationResult Source) : IDiffAnnotation;

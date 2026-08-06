using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitDelta.AI.Agent;
using GitDelta.Core;
using GitDelta.Core.AI;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using Microsoft.Extensions.Logging;

namespace GitDelta.AI;

/// <summary>
/// Orchestrates Phase 3 AI review: gating on privacy settings, materialising a read-only working
/// tree, driving an agent session through change-briefing / file-depth / chat turns via custom tools,
/// and persisting everything through <see cref="IAiResultStore"/> so results survive restarts.
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

    private readonly AiRunStateStore _runStore = new();
    private readonly ConcurrentDictionary<string, RepoObservers> _observersByRepo = new();
    private readonly ConcurrentDictionary<string, List<AiAnnotationResult>> _annotationsByBlob = new();
    private AiReviewQueries? _queries;
    private AiReviewChatActions? _chat;

    private AiReviewQueries Queries => _queries ??= new AiReviewQueries(
        resultStore,
        settingsStore,
        workingCopyMaterialiser,
        _runStore,
        _annotationsByBlob,
        RegisterRunContext);

    private AiReviewChatActions Chat => _chat ??= new AiReviewChatActions(
        resultStore,
        prompts,
        RunTurnForTextAsync);

    // ---------------------------------------------------------------------
    // Cache-only reads (never touch the agent).
    // ---------------------------------------------------------------------

    public ValueTask<AiRunSnapshot?> GetCachedRunAsync(string sessionKey, CancellationToken ct = default) =>
        Queries.GetCachedRunAsync(sessionKey, ct);

    public ValueTask<AiRunSnapshot?> TryGetMatchingCachedRunAsync(
        AiReviewRequest request,
        CancellationToken ct = default) =>
        Queries.TryGetMatchingCachedRunAsync(request, ct);

    public async Task AttachCachedRunAsync(AiReviewRequest request, CancellationToken ct = default) =>
        _ = await TryGetMatchingCachedRunAsync(request, ct).ConfigureAwait(false);

    public ValueTask<IReadOnlyList<IDiffAnnotation>> GetAnnotationsAsync(FileDiffKey key, CancellationToken ct = default) =>
        Queries.GetAnnotationsAsync(key, ct);

    public ValueTask<AiFileBriefingResult?> GetFileBriefingAsync(
        string sessionKey,
        string path,
        string? beforeBlobOid = null,
        string? afterBlobOid = null,
        CancellationToken ct = default) =>
        Queries.GetFileBriefingAsync(sessionKey, path, beforeBlobOid, afterBlobOid, EffectiveRules, ct);

    public ValueTask<IReadOnlyList<AiAnnotationResult>> GetFileAnnotationsAsync(
        string sessionKey,
        string path,
        bool includeDismissed = false,
        CancellationToken ct = default) =>
        Queries.GetFileAnnotationsAsync(sessionKey, path, includeDismissed, ct);

    public Task SetAnnotationReadStateAsync(string annotationId, AiAnnotationReadState state, CancellationToken ct = default) =>
        Queries.SetAnnotationReadStateAsync(annotationId, state, ct);

    public ValueTask<IReadOnlyList<AiChatMessage>> GetChatHistoryAsync(string sessionKey, CancellationToken ct = default) =>
        Queries.GetChatHistoryAsync(sessionKey, ct);

    public Task ClearChatHistoryAsync(string sessionKey, CancellationToken ct = default) =>
        Queries.ClearChatHistoryAsync(sessionKey, ct);

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
        var cacheKey = AiCacheKeys.ComputeChangeBriefingKey(
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
                    var briefing = Deserialize<AiChangeBriefingResult>(cachedResult.PayloadJson);
                    var context = RegisterRunContext(request, cachedRun.Id, cachedRun.CopilotSessionId, briefing, AiRunState.Complete);
                    context.CacheKey = cacheKey;
                    return AiReviewQueries.ToSnapshot(cachedRun, briefing);
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
        var runContext = RegisterRunContext(request, runId, resumeSessionId, briefing: null, AiRunState.Running);
        runContext.CacheKey = cacheKey;
        runContext.StartedUtc = startedUtc;
        runContext.UserCancelled = false;
        runContext.TurnIdleTimedOut = false;
        runContext.RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var configuredRunTimeout = settingsStore.Current.AiRunTimeoutSeconds;
        var runTimeoutSeconds = configuredRunTimeout <= 0 ? 0 : Math.Max(30, configuredRunTimeout);
        runContext.RunTimeoutSeconds = runTimeoutSeconds;
        if (runTimeoutSeconds > 0)
            runContext.RunCts.CancelAfter(TimeSpan.FromSeconds(runTimeoutSeconds));

        await resultStore.UpsertRunAsync(new AiRunRecord(
            runId, request.SessionKey, request.HeadSha, request.MergeBaseSha, CopilotSessionId: resumeSessionId,
            AiRunState.Running, TurnsUsed: 0, request.AdHocInstructions, cacheKey, ErrorMessage: null,
            startedUtc, FinishedUtc: null), ct).ConfigureAwait(false);

        AgentPermissionPolicy? policy = null;
        try
        {
            var workCt = runContext.RunCts.Token;
            policy = await OpenOrResumeSessionAsync(runContext, resumeSessionId, workCt).ConfigureAwait(false);

            await resultStore.UpsertRunAsync(new AiRunRecord(
                runId, request.SessionKey, request.HeadSha, request.MergeBaseSha, runContext.CopilotSessionId,
                AiRunState.Running, runContext.TurnsUsed, request.AdHocInstructions, cacheKey, ErrorMessage: null,
                startedUtc, FinishedUtc: null), workCt).ConfigureAwait(false);

            await RunChangeBriefingTurnAsync(runContext, workCt).ConfigureAwait(false);

            var settings = settingsStore.Current;
            var eligible = request.ChangedFiles
                .Where(f => FileBriefingEligibility.IsEligible(f, settings))
                .ToList();
            runContext.FilesTotal = eligible.Count;
            runContext.FilesCompleted = 0;
            NotifyProgress(runContext, AiRunStage.FileDepth,
                eligible.Count == 0
                    ? "Change briefing complete"
                    : $"Waiting on: file briefings (0/{eligible.Count})");

            foreach (var file in eligible)
            {
                workCt.ThrowIfCancellationRequested();
                await RunFileBriefingTurnAsync(
                        runContext,
                        new AiFileDepthRequest(
                            request.SessionKey,
                            file.Path,
                            file.BeforeBlobOid,
                            file.AfterBlobOid,
                            IncludeAnnotations: true,
                            file.ChangePercent,
                            file.LinesAdded,
                            file.LinesRemoved),
                        workCt)
                    .ConfigureAwait(false);
                runContext.FilesCompleted++;
                NotifyProgress(runContext, AiRunStage.FileDepth,
                    $"Waiting on: file briefings ({runContext.FilesCompleted}/{runContext.FilesTotal})");
            }

            LogPermissionDenials(runContext, policy);
            return await FinishRunAsync(runContext, AiRunState.Complete, errorMessage: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runContext.UserCancelled || ct.IsCancellationRequested || runContext.RunCts.IsCancellationRequested)
        {
            LogPermissionDenials(runContext, policy);
            return await FinishRunAsync(runContext, AiRunState.Incomplete, ClassifyCancellation(runContext), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("turn budget", StringComparison.OrdinalIgnoreCase))
        {
            LogPermissionDenials(runContext, policy);
            return await FinishRunAsync(runContext, AiRunState.PausedBudget, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI review failed for session {SessionKey}.", request.SessionKey);
            LogPermissionDenials(runContext, policy);
            return await FinishRunAsync(runContext, AiRunState.Failed, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    public Task CancelAsync(string repositoryKey, CancellationToken ct = default)
    {
        _ = ct;
        workQueue.CancelRepository(repositoryKey);
        _runStore.CancelRepository(repositoryKey);
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
            try
            {
                await RunFileBriefingTurnAsync(context, request, workCt).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "File-depth review failed for {Path} on session {SessionKey}.", request.Path, request.SessionKey);
                completion.TrySetException(ex);
            }
        }));

        await completion.Task.ConfigureAwait(false);
    }

    public Task<string> AskAsync(AiQuestionRequest request, CancellationToken ct = default) =>
        Chat.AskAsync(request, ct);

    public Task<string> RunInlineActionAsync(AiInlineActionRequest request, CancellationToken ct = default) =>
        Chat.RunInlineActionAsync(request, ct);

    public Task<string> ChatAsync(AiQuestionRequest request, CancellationToken ct = default) =>
        Chat.ChatAsync(request, ct);

    public async Task ClearAiDataAsync(CancellationToken ct = default)
    {
        await resultStore.ClearAllAsync(ct).ConfigureAwait(false);
        await materialiser.ClearAllExportsAsync(ct).ConfigureAwait(false);
        _runStore.Clear();
        _annotationsByBlob.Clear();
    }

    public ValueTask DisposeAsync() => _runStore.DisposeSessionsAsync();

    // ---------------------------------------------------------------------
    // Run implementation.
    // ---------------------------------------------------------------------

    private async Task RunChangeBriefingTurnAsync(AiActiveRunState context, CancellationToken ct)
    {
        NotifyProgress(context, AiRunStage.ChangeBriefing, "Waiting on: change briefing");
        NotifyActivityLog(context, "Waiting on: change briefing");

        var facts = factsAssembler.BuildFactsBlock(context.Request);
        var prompt = prompts.GetChangeBriefingPrompt(new Dictionary<string, string>
        {
            ["rules"] = EffectiveRules(),
            ["facts"] = facts,
            ["adhoc_instructions"] = context.AdHocInstructions ?? "(none)",
        });

        context.AwaitingChangeBriefing = true;
        try
        {
            context.TurnsUsed++;
            await SendTurnWithBudgetAsync(context, prompt, ct).ConfigureAwait(false);
        }
        finally
        {
            context.AwaitingChangeBriefing = false;
        }

        if (context.ChangeBriefing is null)
            throw new InvalidOperationException("The agent did not submit a change briefing.");
    }

    private async Task RunFileBriefingTurnAsync(AiActiveRunState context, AiFileDepthRequest request, CancellationToken ct)
    {
        context.CurrentFileRequest = new FileDepthContext(
            request.Path,
            request.BeforeBlobOid,
            request.AfterBlobOid,
            request.ChangePercent,
            request.LinesAdded,
            request.LinesRemoved);
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
                ["change_percent"] = request.ChangePercent?.ToString() ?? "unknown",
                ["lines_added"] = (request.LinesAdded ?? 0).ToString(),
                ["lines_removed"] = (request.LinesRemoved ?? 0).ToString(),
            };
            var prompt = prompts.GetFileBriefingPrompt(placeholders);

            context.TurnsUsed++;
            await SendTurnWithBudgetAsync(context, prompt, ct).ConfigureAwait(false);
        }
        finally
        {
            context.CurrentFileRequest = null;
        }
    }

    private async Task<AgentPermissionPolicy> OpenOrResumeSessionAsync(
        AiActiveRunState context,
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

    private static readonly TimeSpan SdkTurnWaitCeiling = TimeSpan.FromDays(1);

    private async Task SendTurnWithBudgetAsync(AiActiveRunState context, string prompt, CancellationToken ct)
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

    private static AiRunStage GuessStage(AiActiveRunState context) =>
        context.CurrentFileRequest is not null
            ? AiRunStage.FileDepth
            : context.AwaitingChangeBriefing
                ? AiRunStage.ChangeBriefing
                : AiRunStage.ChangeBriefing;

    private string ClassifyCancellation(AiActiveRunState context)
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

    private void LogPermissionDenials(AiActiveRunState context, AgentPermissionPolicy? policy)
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

    private async Task<AiRunSnapshot> FinishRunAsync(AiActiveRunState context, AiRunState state, string? errorMessage, CancellationToken ct)
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
            context.TurnsUsed, context.AdHocInstructions, context.ChangeBriefing, errorMessage, context.StartedUtc, context.FinishedUtc);
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

    private async Task<AiActiveRunState> EnsureLiveSessionAsync(string sessionKey, CancellationToken ct)
    {
        if (!_runStore.TryGet(sessionKey, out var context))
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

    private AiActiveRunState RegisterRunContext(
        AiReviewRequest request,
        string runId,
        string? copilotSessionId,
        AiChangeBriefingResult? briefing,
        AiRunState state) =>
        _runStore.Register(
            request,
            runId,
            copilotSessionId,
            briefing,
            state,
            settingsStore.Current.AiModelOverride,
            AiCacheKeys.Hash(EffectiveRules()),
            AiCacheKeys.Hash(request.AdHocInstructions));

    private void NotifyProgress(AiActiveRunState context, AiRunStage stage, string? message)
    {
        if (!_observersByRepo.TryGetValue(context.RepositoryKey, out var observers))
            return;

        var elapsed = DateTimeOffset.UtcNow - context.StartedUtc;
        observers.NotifyProgress(new AiRunProgress(
            stage, context.TurnsUsed, settingsStore.Current.AiTurnBudget,
            context.FilesCompleted, context.FilesTotal, elapsed, message));
    }

    private void NotifyActivityLog(AiActiveRunState context, string line)
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
            CopilotSessionId: null, TurnsUsed: 0, request.AdHocInstructions, ChangeBriefing: null, reason, now, now);
    }

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    // ---------------------------------------------------------------------
    // Custom tools invoked by the agent.
    // ---------------------------------------------------------------------

    private IReadOnlyList<AgentCustomTool> BuildTools(AiActiveRunState context) =>
    [
        new AgentCustomTool(
            "submit_change_briefing",
            "Submit the change-level briefing for this review (executive summary, risk, focus, testing, dependencies).",
            (argsJson, ct) => HandleSubmitChangeBriefing(context, argsJson, ct)),
        new AgentCustomTool(
            "submit_file_briefing",
            "Submit the briefing for the file currently being reviewed in depth.",
            (argsJson, ct) => HandleSubmitFileBriefing(context, argsJson, ct)),
        new AgentCustomTool(
            "add_annotation",
            "Add an inline review annotation at a specific location in the file currently being reviewed. Prefer this over burying line-specific issues in findings; call once per location before submit_file_briefing.",
            (argsJson, ct) => HandleAddAnnotation(context, argsJson, ct)),
    ];

    private async Task<string> HandleSubmitChangeBriefing(AiActiveRunState context, string argsJson, CancellationToken ct)
    {
        try
        {
            var payload = Deserialize<AiChangeBriefingResult>(argsJson)
                ?? throw new InvalidOperationException("Empty change briefing payload.");

            var measured = factsAssembler.ComputeMeasuredFacts(context.Request);
            var briefing = payload with
            {
                RiskDrivers = payload.RiskDrivers ?? [],
                WhatChanged = payload.WhatChanged ?? [],
                ReviewFocus = payload.ReviewFocus ?? [],
                TestingStatus = payload.TestingStatus ?? new AiTestingStatus("", []),
                Dependencies = payload.Dependencies ?? [],
                Measured = measured,
                DiagramMermaid = MermaidSourceNormalizer.Normalize(payload.DiagramMermaid),
            };

            context.ChangeBriefing = briefing;

            await resultStore.UpsertPrResultAsync(new AiPrResultRecord(
                context.RunId ?? "",
                context.SessionKey,
                context.CacheKey ?? "",
                Serialize(briefing),
                DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

            return """{"status":"ok"}""";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process submit_change_briefing payload for session {SessionKey}.", context.SessionKey);
            return Serialize(new ToolErrorResult(ex.Message));
        }
    }

    private async Task<string> HandleSubmitFileBriefing(AiActiveRunState context, string argsJson, CancellationToken ct)
    {
        try
        {
            var briefing = Deserialize<AiFileBriefingResult>(argsJson)
                ?? throw new InvalidOperationException("Empty file briefing payload.");
            var fileContext = context.CurrentFileRequest
                ?? throw new InvalidOperationException("No file-depth request is currently in progress.");

            // Drop quality when change percent is not above 50.
            if (fileContext.ChangePercent is null or <= 50)
                briefing = briefing with { QualityScore = null, QualityRationale = null };

            briefing = briefing with { Findings = briefing.Findings ?? [] };

            var cacheKey = AiCacheKeys.ComputeFileKey(
                fileContext.Path, fileContext.BeforeOid, fileContext.AfterOid, AiPromptCatalog.PromptVersion,
                context.Model, context.RulesHash ?? "", context.InstructionsHash ?? "");

            var record = new AiFileResultRecord(
                context.RunId ?? "",
                context.SessionKey,
                briefing.Path,
                cacheKey,
                briefing.Classification.ToString(),
                Serialize(briefing),
                DateTimeOffset.UtcNow);

            await resultStore.UpsertFileResultAsync(record, ct).ConfigureAwait(false);
            return """{"status":"ok"}""";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process submit_file_briefing payload for session {SessionKey}.", context.SessionKey);
            return Serialize(new ToolErrorResult(ex.Message));
        }
    }

    private async Task<string> HandleAddAnnotation(AiActiveRunState context, string argsJson, CancellationToken ct)
    {
        try
        {
            var args = Deserialize<AddAnnotationArgs>(argsJson) ?? throw new InvalidOperationException("Empty annotation payload.");
            var blobOid = ResolveAnnotationBlobOid(args.BlobOid, args.Side, context.CurrentFileRequest);
            if (string.IsNullOrWhiteSpace(blobOid))
            {
                return Serialize(new ToolErrorResult(
                    "blobOid is required. For side=New use the After blob from the File header; for side=Old use Before. Do not use 'New', 'Old', or '(new file)' as blobOid."));
            }

            var id = Guid.NewGuid().ToString("N");

            await resultStore.UpsertAnnotationAsync(new AiAnnotationRecord(
                id, context.RunId ?? "", context.SessionKey, args.Path, blobOid, args.StartLine, args.EndLine,
                args.Side.ToString(), args.Severity.ToString(), args.Body, AiAnnotationReadState.Unread, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);

            var result = new AiAnnotationResult(
                id, args.Path, blobOid, args.StartLine, args.EndLine, args.Side, args.Severity, args.Body, AiAnnotationReadState.Unread);

            var list = _annotationsByBlob.GetOrAdd(blobOid, static _ => []);
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

    /// <summary>
    /// Maps side labels / placeholders the model sometimes sends as <c>blobOid</c> to the real
    /// before/after OID from the current file turn. Returns null when no usable OID is available.
    /// </summary>
    internal static string? ResolveAnnotationBlobOid(string? blobOid, DiffSide side, FileDepthContext? file)
    {
        if (!IsMissingOrPlaceholderBlobOid(blobOid))
            return blobOid;

        if (file is null)
            return null;

        return side == DiffSide.Old ? file.BeforeOid : file.AfterOid;
    }

    private static bool IsMissingOrPlaceholderBlobOid(string? blobOid)
    {
        if (string.IsNullOrWhiteSpace(blobOid))
            return true;

        return blobOid.Equals("New", StringComparison.OrdinalIgnoreCase)
               || blobOid.Equals("Old", StringComparison.OrdinalIgnoreCase)
               || blobOid.Equals("(new file)", StringComparison.OrdinalIgnoreCase)
               || blobOid.Equals("(deleted)", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ToolErrorResult(string Message)
    {
        public string Status => "error";
    }

    private sealed record AddAnnotationArgs(
        string Path,
        string? BlobOid,
        int StartLine,
        int EndLine,
        DiffSide Side,
        AiAnnotationSeverity Severity,
        string Body);

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

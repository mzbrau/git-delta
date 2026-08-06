using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.AI;
using GitDelta.Core.Diff;

namespace GitDelta.AI;

/// <summary>
/// Cache-only / read-model queries for AI review results. Never starts an agent turn.
/// </summary>
internal sealed class AiReviewQueries(
    IAiResultStore resultStore,
    ISettingsStore settingsStore,
    WorkingCopyMaterialiser workingCopyMaterialiser,
    AiRunStateStore runStore,
    ConcurrentDictionary<string, List<AiAnnotationResult>> annotationsByBlob,
    Func<AiReviewRequest, string, string?, AiChangeBriefingResult?, AiRunState, AiActiveRunState> registerRun)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask<AiRunSnapshot?> GetCachedRunAsync(string sessionKey, CancellationToken ct = default)
    {
        var run = await resultStore.GetLatestRunAsync(sessionKey, ct).ConfigureAwait(false);
        if (run is null)
            return null;

        var prResult = await resultStore.GetPrResultForRunAsync(run.Id, ct).ConfigureAwait(false);
        var briefing = prResult is null ? null : Deserialize<AiChangeBriefingResult>(prResult.PayloadJson);
        return ToSnapshot(run, briefing);
    }

    public async ValueTask<AiRunSnapshot?> TryGetMatchingCachedRunAsync(
        AiReviewRequest request,
        CancellationToken ct = default)
    {
        if (IsWorkingCopyScope(request.Scope))
        {
            if (string.IsNullOrWhiteSpace(request.RepositoryPath))
                return null;

            var treeOid = await workingCopyMaterialiser
                .WriteTreeAsync(request.RepositoryPath, request.Scope, ct)
                .ConfigureAwait(false);
            request = request with { HeadSha = treeOid };
        }

        var run = await resultStore.GetLatestRunAsync(request.SessionKey, ct).ConfigureAwait(false);
        if (run is null)
            return null;

        if (!string.Equals(run.HeadSha, request.HeadSha, StringComparison.Ordinal) ||
            !string.Equals(run.MergeBaseSha, request.MergeBaseSha, StringComparison.Ordinal))
            return null;

        if (runStore.TryGet(request.SessionKey, out var existing) && existing.Session is not null)
        {
            var existingBriefing = existing.ChangeBriefing;
            return ToSnapshot(run, existingBriefing);
        }

        var prResult = await resultStore.GetPrResultForRunAsync(run.Id, ct).ConfigureAwait(false);
        var briefing = prResult is null ? null : Deserialize<AiChangeBriefingResult>(prResult.PayloadJson);
        var context = registerRun(request, run.Id, run.CopilotSessionId, briefing, run.State);
        context.CacheKey = run.CacheKey;
        context.TurnsUsed = run.TurnsUsed;
        context.StartedUtc = run.StartedUtc;
        context.FinishedUtc = run.FinishedUtc;
        context.ErrorMessage = run.ErrorMessage;
        return ToSnapshot(run, briefing);
    }

    public ValueTask<IReadOnlyList<IDiffAnnotation>> GetAnnotationsAsync(FileDiffKey key, CancellationToken ct = default)
    {
        _ = ct;
        var results = new List<IDiffAnnotation>();
        if (annotationsByBlob.TryGetValue(key.OldContent.Value, out var oldAnnotations))
        {
            lock (oldAnnotations)
                results.AddRange(oldAnnotations.Select(ToDiffAnnotation));
        }

        if (annotationsByBlob.TryGetValue(key.NewContent.Value, out var newAnnotations))
        {
            lock (newAnnotations)
                results.AddRange(newAnnotations.Select(ToDiffAnnotation));
        }

        return ValueTask.FromResult<IReadOnlyList<IDiffAnnotation>>(results);
    }

    public async ValueTask<AiFileBriefingResult?> GetFileBriefingAsync(
        string sessionKey,
        string path,
        string? beforeBlobOid,
        string? afterBlobOid,
        Func<string> effectiveRules,
        CancellationToken ct = default)
    {
        var run = await resultStore.GetLatestRunAsync(sessionKey, ct).ConfigureAwait(false);
        if (run is null)
            return null;

        var rulesHash = AiCacheKeys.Hash(effectiveRules());
        var instructionsHash = AiCacheKeys.Hash(run.AdHocInstructions);
        var model = settingsStore.Current.AiModelOverride;

        var exact = await TryGetFileBriefingByOidsAsync(
                sessionKey, path, beforeBlobOid, afterBlobOid, model, rulesHash, instructionsHash, ct)
            .ConfigureAwait(false);
        if (exact is not null)
            return exact;

        // Pull-request eager briefings are keyed with null blob OIDs (facts omit them). When the
        // caller supplies concrete diff OIDs and the exact key misses, accept that null-OID record
        // rather than falling back to a path-only hit from an unrelated change.
        if (beforeBlobOid is not null || afterBlobOid is not null)
        {
            return await TryGetFileBriefingByOidsAsync(
                    sessionKey, path, beforeBlobOid: null, afterBlobOid: null, model, rulesHash, instructionsHash, ct)
                .ConfigureAwait(false);
        }

        return null;
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

    private async ValueTask<AiFileBriefingResult?> TryGetFileBriefingByOidsAsync(
        string sessionKey,
        string path,
        string? beforeBlobOid,
        string? afterBlobOid,
        string? model,
        string rulesHash,
        string instructionsHash,
        CancellationToken ct)
    {
        var cacheKey = AiCacheKeys.ComputeFileKey(
            path,
            beforeBlobOid,
            afterBlobOid,
            AiPromptCatalog.PromptVersion,
            model,
            rulesHash,
            instructionsHash);

        var match = await resultStore.GetFileResultByCacheKeyAsync(cacheKey, ct).ConfigureAwait(false);
        if (match is null ||
            !string.Equals(match.SessionKey, sessionKey, StringComparison.Ordinal) ||
            !string.Equals(match.Path, path, StringComparison.Ordinal))
            return null;

        return match.SummaryJson is null ? null : Deserialize<AiFileBriefingResult>(match.SummaryJson);
    }

    internal static AiRunSnapshot ToSnapshot(AiRunRecord run, AiChangeBriefingResult? briefing) => new(
        run.Id, run.SessionKey, run.HeadSha, run.MergeBaseSha, run.State, run.CopilotSessionId,
        run.TurnsUsed, run.AdHocInstructions, briefing, run.ErrorMessage, run.StartedUtc, run.FinishedUtc);

    internal static IDiffAnnotation ToDiffAnnotation(AiAnnotationResult result)
    {
        var content = new ContentId(result.BlobOid);
        var range = new AnnotationRange(
            new DiffAnchor(result.Side, content, result.StartLine),
            new DiffAnchor(result.Side, content, result.EndLine));
        return new AiDiffAnnotation(range, result);
    }

    internal static AiAnnotationResult ToAnnotationResult(AiAnnotationRecord record) => new(
        record.Id, record.Path, record.BlobOid, record.StartLine, record.EndLine,
        Enum.Parse<DiffSide>(record.Side), Enum.Parse<AiAnnotationSeverity>(record.Severity), record.Body, record.ReadState);

    private static bool IsWorkingCopyScope(AiReviewScope scope) =>
        scope is AiReviewScope.WorkingCopyStaged or AiReviewScope.WorkingCopyAll;

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);
}

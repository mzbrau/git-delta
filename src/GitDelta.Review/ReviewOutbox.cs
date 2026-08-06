using System.Net.Http;
using GitDelta.GitHub;
using GitDelta.Persistence;

namespace GitDelta.Review;

internal sealed class ReviewOutbox(
    IDurableUserStore durableStore,
    ReviewMutationExecutor mutationExecutor) : IReviewOutbox
{
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private bool _isOffline;

    public bool IsOffline => _isOffline;

    public event EventHandler? DrainCompleted;

    public async Task EnqueueAsync(OutboxEntry entry, CancellationToken ct = default)
    {
        durableStore.EnsureSchema();
        await durableStore.EnqueueAsync(entry, ct).ConfigureAwait(false);
        if (!_isOffline)
            await DrainAsync(ct).ConfigureAwait(false);
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        if (_isOffline)
            return;

        if (!await _drainGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            durableStore.EnsureSchema();
            await durableStore.RecoverInFlightAsync(ct).ConfigureAwait(false);

            var pending = await durableStore.ListAsync(OutboxState.Pending, prNodeId: null, ct)
                .ConfigureAwait(false);

            foreach (var entry in pending.Where(e => e.Kind != OutboxKind.SubmitReview))
            {
                ct.ThrowIfCancellationRequested();
                await ProcessEntryAsync(entry, ct).ConfigureAwait(false);
            }

            _isOffline = false;
        }
        catch (GitHubApiException ex) when (IsOfflineException(ex))
        {
            _isOffline = true;
        }
        catch (HttpRequestException)
        {
            _isOffline = true;
        }
        finally
        {
            _drainGate.Release();
            DrainCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task DrainSubmitAsync(string entryId, CancellationToken ct = default)
    {
        durableStore.EnsureSchema();
        var entries = await durableStore.ListAsync(OutboxState.Pending, prNodeId: null, ct)
            .ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Id == entryId && e.Kind == OutboxKind.SubmitReview)
            ?? throw new InvalidOperationException($"Submit outbox entry {entryId} not found.");

        await ProcessEntryAsync(entry, ct).ConfigureAwait(false);
        DrainCompleted?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<OutboxEntry>> ListPendingAsync(
        string? prNodeId = null,
        CancellationToken ct = default)
    {
        durableStore.EnsureSchema();
        return await durableStore.ListAsync(OutboxState.Pending, prNodeId, ct).ConfigureAwait(false);
    }

    private async Task ProcessEntryAsync(OutboxEntry entry, CancellationToken ct)
    {
        try
        {
            await durableStore.MarkInFlightAsync(entry.Id, ct).ConfigureAwait(false);
            await mutationExecutor.ExecuteOutboxEntryAsync(entry, ct).ConfigureAwait(false);
            await durableStore.DeleteAsync(entry.Id, ct).ConfigureAwait(false);
            _isOffline = false;
        }
        catch (HeadMovedException)
        {
            await durableStore.MarkFailedAsync(entry.Id, "Head moved.", ct).ConfigureAwait(false);
            throw;
        }
        catch (GitHubApiException ex) when (IsOfflineException(ex))
        {
            await durableStore.MarkPendingAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);
            _isOffline = true;
        }
        catch (HttpRequestException ex)
        {
            await durableStore.MarkPendingAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);
            _isOffline = true;
        }
        catch (Exception ex)
        {
            await durableStore.MarkFailedAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsOfflineException(GitHubApiException ex) =>
        ex.StatusCode is 0 or 408 or 502 or 503 or 504;
}

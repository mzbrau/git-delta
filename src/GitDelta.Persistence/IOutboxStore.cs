namespace GitDelta.Persistence;

public interface IOutboxStore
{
    Task EnqueueAsync(OutboxEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<OutboxEntry>> ListAsync(
        OutboxState? state = null,
        string? prNodeId = null,
        CancellationToken ct = default);

    Task MarkInFlightAsync(string id, CancellationToken ct = default);

    Task MarkPendingAsync(string id, string? lastError = null, CancellationToken ct = default);

    Task MarkFailedAsync(string id, string error, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    Task RecoverInFlightAsync(CancellationToken ct = default);
}

namespace CodeReviewr.Persistence;

public interface ILocalNotesStore
{
    Task<string?> GetNoteAsync(string prNodeId, CancellationToken ct = default);

    Task SetNoteAsync(string prNodeId, string markdown, CancellationToken ct = default);
}

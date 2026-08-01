namespace ResilientWorkerKit;

/// <summary>Persists dead-letter records for later inspection or reprocessing.</summary>
public interface IDeadLetterStore
{
    /// <summary>Adds a dead-letter record.</summary>
    Task AddAsync(DeadLetterRecord record, CancellationToken cancellationToken = default);

    /// <summary>Returns unprocessed records (optionally for one job), oldest first.</summary>
    Task<IReadOnlyList<DeadLetterRecord>> GetPendingAsync(string? jobId, int maxCount, CancellationToken cancellationToken = default);

    /// <summary>Marks a record as reprocessed.</summary>
    Task MarkReprocessedAsync(string id, CancellationToken cancellationToken = default);
}

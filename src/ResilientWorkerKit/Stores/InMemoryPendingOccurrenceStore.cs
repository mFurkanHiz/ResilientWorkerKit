using System.Collections.Concurrent;

namespace ResilientWorkerKit.Stores;

/// <summary>
/// In-memory pending-occurrence queue for tests and demos. <b>Not suitable for production</b>:
/// the whole point of a follow-up retry is surviving a restart, and this implementation loses
/// the queue with the process. Use the EF Core store for durable planned occurrences.
/// </summary>
public sealed class InMemoryPendingOccurrenceStore : IPendingOccurrenceStore
{
    private readonly ConcurrentDictionary<string, PendingOccurrence> _occurrences = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task AddAsync(PendingOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        _occurrences[occurrence.Id] = occurrence;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PendingOccurrence?> GetNextAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var next = _occurrences.Values
            .Where(o => o.JobId == jobId)
            .OrderBy(o => o.DueAtUtc)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return Task.FromResult(next);
    }

    /// <inheritdoc />
    public Task<bool> TryClaimAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_occurrences.TryRemove(id, out _));

    /// <inheritdoc />
    public Task<int> CountAsync(string jobId, CancellationToken cancellationToken = default)
        => Task.FromResult(_occurrences.Values.Count(o => o.JobId == jobId));
}

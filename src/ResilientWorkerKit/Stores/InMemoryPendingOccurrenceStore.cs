namespace ResilientWorkerKit.Stores;

/// <summary>
/// In-memory pending-occurrence queue for tests and demos. <b>Not suitable for production</b>:
/// the whole point of a follow-up retry is surviving a restart, and this implementation loses
/// the queue with the process. Use the EF Core store for durable planned occurrences.
/// <para>
/// Lease semantics match the contract exactly (single winner, token-checked operations,
/// visibility-based expiry), so engine behaviour under test is the same as against a database.
/// </para>
/// </summary>
public sealed class InMemoryPendingOccurrenceStore : IPendingOccurrenceStore
{
    private sealed class Entry
    {
        public required PendingOccurrence Occurrence { get; set; }
        public string? LeaseToken { get; set; }
        public string? LeaseOwner { get; set; }
        public DateTimeOffset? ClaimedAtUtc { get; set; }
        public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task<bool> AddAsync(PendingOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        lock (_gate)
        {
            var duplicate = _entries.Values.Any(e =>
                e.Occurrence.JobId == occurrence.JobId
                && e.Occurrence.IdentityToken == occurrence.IdentityToken);
            if (duplicate)
            {
                return Task.FromResult(false);
            }

            _entries[occurrence.Id] = new Entry { Occurrence = occurrence };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<PendingOccurrence?> GetNextAsync(string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var next = _entries.Values
                .Where(e => e.Occurrence.JobId == jobId)
                .OrderBy(e => EffectiveAt(e, nowUtc))
                .ThenBy(e => e.Occurrence.Id, StringComparer.Ordinal)
                .FirstOrDefault();

            return Task.FromResult(next is null ? null : Snapshot(next));
        }
    }

    /// <inheritdoc />
    public Task<string?> TryAcquireLeaseAsync(
        string id, string owner, TimeSpan duration, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Inclusive expiry boundary, matching GetNextAsync's visibility: a row surfaced AT
            // its lease expiry is acquirable at that same instant.
            if (!_entries.TryGetValue(id, out var entry)
                || (entry.LeaseToken is not null && entry.LeaseExpiresAtUtc > nowUtc))
            {
                return Task.FromResult<string?>(null);
            }

            var token = Guid.NewGuid().ToString("n");
            entry.LeaseToken = token;
            entry.LeaseOwner = owner;
            entry.ClaimedAtUtc = nowUtc;
            entry.LeaseExpiresAtUtc = nowUtc + duration;
            return Task.FromResult<string?>(token);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryRenewLeaseAsync(
        string id, string leaseToken, TimeSpan duration, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.LeaseToken != leaseToken)
            {
                return Task.FromResult(false);
            }

            entry.LeaseExpiresAtUtc = nowUtc + duration;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.LeaseToken != leaseToken)
            {
                return Task.FromResult(false);
            }

            _entries.Remove(id);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.LeaseToken != leaseToken)
            {
                return Task.FromResult(false);
            }

            entry.LeaseToken = null;
            entry.LeaseOwner = null;
            entry.ClaimedAtUtc = null;
            entry.LeaseExpiresAtUtc = null;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<int> CountAsync(string jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.Values.Count(e => e.Occurrence.JobId == jobId));
        }
    }

    private static DateTimeOffset EffectiveAt(Entry entry, DateTimeOffset nowUtc)
    {
        var due = entry.Occurrence.DueAtUtc;
        return entry.LeaseToken is not null && entry.LeaseExpiresAtUtc is { } expiry
            && expiry >= nowUtc && expiry > due
                ? expiry
                : due;
    }

    private static PendingOccurrence Snapshot(Entry entry)
        => new()
        {
            Id = entry.Occurrence.Id,
            JobId = entry.Occurrence.JobId,
            DueAtUtc = entry.Occurrence.DueAtUtc,
            IdentityToken = entry.Occurrence.IdentityToken,
            Source = entry.Occurrence.Source,
            OriginScheduledExecutionId = entry.Occurrence.OriginScheduledExecutionId,
            FollowUpOrdinal = entry.Occurrence.FollowUpOrdinal,
            PayloadJson = entry.Occurrence.PayloadJson,
            CreatedAtUtc = entry.Occurrence.CreatedAtUtc,
            LeaseOwner = entry.LeaseOwner,
            ClaimedAtUtc = entry.ClaimedAtUtc,
            LeaseExpiresAtUtc = entry.LeaseExpiresAtUtc,
        };
}

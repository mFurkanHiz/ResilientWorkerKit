using Microsoft.EntityFrameworkCore;

namespace ResilientWorkerKit.EntityFrameworkCore;

/// <summary>EF Core execution-history store.</summary>
public sealed class EfCoreJobExecutionStore : IJobExecutionStore
{
    private readonly IDbContextFactory<WorkerKitDbContext> _factory;

    /// <summary>Creates the store.</summary>
    public EfCoreJobExecutionStore(IDbContextFactory<WorkerKitDbContext> factory) => _factory = factory;

    /// <inheritdoc />
    public async Task CreateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            db.Executions.Add(ToEntity(record));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(JobExecutionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entity = await db.Executions.FindAsync([record.ExecutionId], cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                db.Executions.Add(ToEntity(record));
            }
            else
            {
                Apply(record, entity);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<JobExecutionRecord?> GetAsync(string executionId, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entity = await db.Executions.AsNoTracking()
                .FirstOrDefaultAsync(e => e.ExecutionId == executionId, cancellationToken).ConfigureAwait(false);
            return entity is null ? null : ToRecord(entity);
        }
    }

    /// <inheritdoc />
    public async Task<JobExecutionRecord?> GetLatestAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entity = await db.Executions.AsNoTracking()
                .Where(e => e.JobId == jobId)
                .OrderByDescending(e => e.StartedAtUtc)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return entity is null ? null : ToRecord(entity);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobExecutionRecord>> GetRecentAsync(string jobId, int count, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entities = await db.Executions.AsNoTracking()
                .Where(e => e.JobId == jobId)
                .OrderByDescending(e => e.StartedAtUtc)
                .Take(count)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return entities.Select(ToRecord).ToList();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsForScheduledExecutionAsync(string jobId, string scheduledExecutionId, bool completedOnly, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var query = db.Executions.AsNoTracking()
                .Where(e => e.JobId == jobId && e.ScheduledExecutionId == scheduledExecutionId);
            if (completedOnly)
            {
                query = query.Where(e => e.Status == JobExecutionStatus.Completed);
            }

            return await query.AnyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<int> MarkRunningAsAbandonedAsync(CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.Executions
                .Where(e => e.Status == JobExecutionStatus.Running)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(e => e.Status, JobExecutionStatus.Abandoned)
                    .SetProperty(e => e.FailureKind, JobFailureKind.Abandoned),
                    cancellationToken).ConfigureAwait(false);
        }
    }

    private static JobExecutionEntity ToEntity(JobExecutionRecord r) => Apply(r, new JobExecutionEntity
    {
        ExecutionId = r.ExecutionId,
        JobId = r.JobId,
        ScheduledExecutionId = r.ScheduledExecutionId,
        ScheduledAtUtc = r.ScheduledAtUtc.UtcDateTime,
        ScheduledLocalTime = r.ScheduledLocalTime,
        TimeZoneId = r.TimeZoneId,
        TriggerType = r.TriggerType,
        StartedAtUtc = r.StartedAtUtc.UtcDateTime,
        CorrelationId = r.CorrelationId,
        HostInstanceId = r.HostInstanceId,
        CreatedAtUtc = r.CreatedAtUtc.UtcDateTime,
    });

    private static JobExecutionEntity Apply(JobExecutionRecord r, JobExecutionEntity e)
    {
        e.CompletedAtUtc = r.CompletedAtUtc?.UtcDateTime;
        e.Status = r.Status;
        e.FailureKind = r.FailureKind;
        e.AttemptCount = r.AttemptCount;
        e.DurationMs = r.DurationMs;
        e.ErrorType = r.ErrorType;
        e.ErrorMessage = r.ErrorMessage;
        e.ErrorDetail = r.ErrorDetail;
        e.LastCheckpointSummary = r.LastCheckpointSummary;
        e.UpdatedAtUtc = r.UpdatedAtUtc.UtcDateTime;
        return e;
    }

    private static JobExecutionRecord ToRecord(JobExecutionEntity e) => new()
    {
        JobId = e.JobId,
        ExecutionId = e.ExecutionId,
        ScheduledExecutionId = e.ScheduledExecutionId,
        ScheduledAtUtc = Utc(e.ScheduledAtUtc),
        ScheduledLocalTime = e.ScheduledLocalTime,
        TimeZoneId = e.TimeZoneId,
        TriggerType = e.TriggerType,
        StartedAtUtc = Utc(e.StartedAtUtc),
        CompletedAtUtc = e.CompletedAtUtc is { } completed ? Utc(completed) : null,
        Status = e.Status,
        FailureKind = e.FailureKind,
        AttemptCount = e.AttemptCount,
        DurationMs = e.DurationMs,
        ErrorType = e.ErrorType,
        ErrorMessage = e.ErrorMessage,
        ErrorDetail = e.ErrorDetail,
        CorrelationId = e.CorrelationId,
        HostInstanceId = e.HostInstanceId,
        LastCheckpointSummary = e.LastCheckpointSummary,
        CreatedAtUtc = Utc(e.CreatedAtUtc),
        UpdatedAtUtc = Utc(e.UpdatedAtUtc),
    };

    internal static DateTimeOffset Utc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>EF Core checkpoint store (one row per job; the save is a single-row upsert).</summary>
public sealed class EfCoreJobCheckpointStore : IJobCheckpointStore
{
    private readonly IDbContextFactory<WorkerKitDbContext> _factory;

    /// <summary>Creates the store.</summary>
    public EfCoreJobCheckpointStore(IDbContextFactory<WorkerKitDbContext> factory) => _factory = factory;

    /// <inheritdoc />
    public async Task<JobCheckpoint?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entity = await db.Checkpoints.AsNoTracking()
                .FirstOrDefaultAsync(c => c.JobId == jobId, cancellationToken).ConfigureAwait(false);
            return entity is null
                ? null
                : new JobCheckpoint(
                    entity.JobId, entity.PayloadJson, entity.PayloadType,
                    EfCoreJobExecutionStore.Utc(entity.UpdatedAtUtc));
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entity = await db.Checkpoints.FindAsync([checkpoint.JobId], cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                db.Checkpoints.Add(new JobCheckpointEntity
                {
                    JobId = checkpoint.JobId,
                    PayloadJson = checkpoint.PayloadJson,
                    PayloadType = checkpoint.PayloadType,
                    UpdatedAtUtc = checkpoint.UpdatedAtUtc.UtcDateTime,
                });
            }
            else
            {
                entity.PayloadJson = checkpoint.PayloadJson;
                entity.PayloadType = checkpoint.PayloadType;
                entity.UpdatedAtUtc = checkpoint.UpdatedAtUtc.UtcDateTime;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.Checkpoints.Where(c => c.JobId == jobId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// EF Core idempotency store. Concurrent acquisitions are settled by the database: the
/// composite primary key rejects the second insert, and the <c>Version</c> concurrency token
/// rejects the second re-acquire update.
/// </summary>
public sealed class EfCoreIdempotencyStore : IIdempotencyStore
{
    private readonly IDbContextFactory<WorkerKitDbContext> _factory;
    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    public EfCoreIdempotencyStore(IDbContextFactory<WorkerKitDbContext> factory, TimeProvider time)
    {
        _factory = factory;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<IdempotencyAcquireResult> TryAcquireAsync(string jobId, string key, string executionId, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expires = expiresAtUtc?.UtcDateTime;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var existing = await db.IdempotencyRecords
                    .FirstOrDefaultAsync(r => r.JobId == jobId && r.Key == key, cancellationToken).ConfigureAwait(false);

                if (existing is null)
                {
                    db.IdempotencyRecords.Add(new JobIdempotencyEntity
                    {
                        JobId = jobId,
                        Key = key,
                        Status = IdempotencyStatus.Pending,
                        ExecutionId = executionId,
                        CreatedAtUtc = now,
                        ExpiresAtUtc = expires,
                        Version = 0,
                    });

                    try
                    {
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        return IdempotencyAcquireResult.Acquired;
                    }
                    catch (DbUpdateException)
                    {
                        continue; // lost the insert race; re-read and re-evaluate
                    }
                }

                var expired = existing.ExpiresAtUtc is { } expiry && expiry <= now;
                if (!expired && existing.Status == IdempotencyStatus.Completed)
                {
                    return IdempotencyAcquireResult.AlreadyCompleted;
                }

                if (!expired && existing.Status == IdempotencyStatus.Pending)
                {
                    return existing.ExecutionId == executionId
                        ? IdempotencyAcquireResult.Acquired
                        : IdempotencyAcquireResult.InProgressElsewhere;
                }

                // Failed or expired: re-acquire under the concurrency token.
                existing.Status = IdempotencyStatus.Pending;
                existing.ExecutionId = executionId;
                existing.ExpiresAtUtc = expires;
                existing.CompletedAtUtc = null;
                existing.Version++;

                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return IdempotencyAcquireResult.Acquired;
                }
                catch (DbUpdateConcurrencyException)
                {
                    continue; // lost the update race; re-read and re-evaluate
                }
            }
        }

        return IdempotencyAcquireResult.InProgressElsewhere;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsCompletedAsync(string jobId, string key, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.IdempotencyRecords.AsNoTracking().AnyAsync(r =>
                r.JobId == jobId && r.Key == key &&
                r.Status == IdempotencyStatus.Completed &&
                (r.ExpiresAtUtc == null || r.ExpiresAtUtc > now), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task MarkCompletedAsync(string jobId, string key, CancellationToken cancellationToken = default)
        => SetStatusAsync(jobId, key, IdempotencyStatus.Completed, cancellationToken);

    /// <inheritdoc />
    public Task MarkFailedAsync(string jobId, string key, CancellationToken cancellationToken = default)
        => SetStatusAsync(jobId, key, IdempotencyStatus.Failed, cancellationToken);

    /// <inheritdoc />
    public async Task<IdempotencyRecord?> GetAsync(string jobId, string key, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entity = await db.IdempotencyRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.JobId == jobId && r.Key == key, cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                return null;
            }

            return new IdempotencyRecord
            {
                JobId = entity.JobId,
                Key = entity.Key,
                Status = entity.Status,
                ExecutionId = entity.ExecutionId,
                CreatedAtUtc = EfCoreJobExecutionStore.Utc(entity.CreatedAtUtc),
                CompletedAtUtc = entity.CompletedAtUtc is { } completed ? EfCoreJobExecutionStore.Utc(completed) : null,
                ExpiresAtUtc = entity.ExpiresAtUtc is { } expiry ? EfCoreJobExecutionStore.Utc(expiry) : null,
            };
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string jobId, string key, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.IdempotencyRecords.Where(r => r.JobId == jobId && r.Key == key)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SetStatusAsync(string jobId, string key, IdempotencyStatus status, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        // Retried like TryAcquireAsync: this runs *after* the side effect succeeded, so losing a
        // concurrency race here must not fail the execution and cause a duplicate later.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var entity = await db.IdempotencyRecords
                    .FirstOrDefaultAsync(r => r.JobId == jobId && r.Key == key, cancellationToken).ConfigureAwait(false);
                if (entity is null)
                {
                    return;
                }

                entity.Status = status;
                entity.CompletedAtUtc = status == IdempotencyStatus.Completed ? now : entity.CompletedAtUtc;
                entity.Version++;

                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Someone else changed the row; re-read and apply again.
                }
            }
        }
    }
}

/// <summary>
/// EF Core pending-occurrence store. This is the store that gives follow-up retries their whole
/// point: a retry queued here outlives the process that queued it.
/// </summary>
public sealed class EfCorePendingOccurrenceStore : IPendingOccurrenceStore
{
    private readonly IDbContextFactory<WorkerKitDbContext> _factory;

    /// <summary>Creates the store.</summary>
    public EfCorePendingOccurrenceStore(IDbContextFactory<WorkerKitDbContext> factory) => _factory = factory;

    /// <inheritdoc />
    public async Task<bool> AddAsync(PendingOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            db.PendingOccurrences.Add(new JobPendingOccurrenceEntity
            {
                Id = occurrence.Id,
                JobId = occurrence.JobId,
                DueAtUtc = occurrence.DueAtUtc.UtcDateTime,
                IdentityToken = occurrence.IdentityToken,
                Source = occurrence.Source,
                OriginScheduledExecutionId = occurrence.OriginScheduledExecutionId,
                FollowUpOrdinal = occurrence.FollowUpOrdinal,
                PayloadJson = occurrence.PayloadJson,
                CreatedAtUtc = occurrence.CreatedAtUtc.UtcDateTime,
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateException)
            {
                // Provider-portable duplicate detection: rather than decoding provider-specific
                // error codes, ask the database whether the logical occurrence now exists. If it
                // does, another writer won the unique (JobId, IdentityToken) index and this add
                // is a duplicate by design; anything else is a real failure.
                db.ChangeTracker.Clear();
                var alreadyQueued = await db.PendingOccurrences.AsNoTracking()
                    .AnyAsync(
                        p => p.JobId == occurrence.JobId && p.IdentityToken == occurrence.IdentityToken,
                        cancellationToken).ConfigureAwait(false);
                if (alreadyQueued)
                {
                    return false;
                }

                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task<PendingOccurrence?> GetNextAsync(
        string jobId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var now = nowUtc.UtcDateTime;
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // Effective time: an unexpired lease hides the row until the lease would expire, so
            // a scheduler can sleep exactly until a dead owner's work becomes acquirable.
            var entity = await db.PendingOccurrences.AsNoTracking()
                .Where(p => p.JobId == jobId)
                .OrderBy(p => p.LeaseToken != null && p.LeaseExpiresAtUtc >= now && p.LeaseExpiresAtUtc > p.DueAtUtc
                    ? p.LeaseExpiresAtUtc!.Value
                    : p.DueAtUtc)
                .ThenBy(p => p.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            return entity is null ? null : new PendingOccurrence
            {
                Id = entity.Id,
                JobId = entity.JobId,
                DueAtUtc = EfCoreJobExecutionStore.Utc(entity.DueAtUtc),
                IdentityToken = entity.IdentityToken,
                Source = entity.Source,
                OriginScheduledExecutionId = entity.OriginScheduledExecutionId,
                FollowUpOrdinal = entity.FollowUpOrdinal,
                PayloadJson = entity.PayloadJson,
                CreatedAtUtc = EfCoreJobExecutionStore.Utc(entity.CreatedAtUtc),
                LeaseOwner = entity.LeaseOwner,
                ClaimedAtUtc = entity.ClaimedAtUtc is { } claimed ? EfCoreJobExecutionStore.Utc(claimed) : null,
                LeaseExpiresAtUtc = entity.LeaseExpiresAtUtc is { } expires ? EfCoreJobExecutionStore.Utc(expires) : null,
            };
        }
    }

    /// <inheritdoc />
    public async Task<string?> TryAcquireLeaseAsync(
        string id, string owner, TimeSpan duration, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var now = nowUtc.UtcDateTime;
        var expiresAt = now + duration;
        var token = Guid.NewGuid().ToString("n");

        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // The conditional update IS the race arbiter: of any number of concurrent callers,
            // the database lets exactly one match the unleased-or-expired predicate.
            var won = await db.PendingOccurrences
                .Where(p => p.Id == id && (p.LeaseToken == null || p.LeaseExpiresAtUtc < now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.LeaseOwner, owner)
                    .SetProperty(p => p.LeaseToken, token)
                    .SetProperty(p => p.ClaimedAtUtc, now)
                    .SetProperty(p => p.LeaseExpiresAtUtc, expiresAt),
                    cancellationToken).ConfigureAwait(false);

            return won > 0 ? token : null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewLeaseAsync(
        string id, string leaseToken, TimeSpan duration, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var expiresAt = nowUtc.UtcDateTime + duration;
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var renewed = await db.PendingOccurrences
                .Where(p => p.Id == id && p.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.LeaseExpiresAtUtc, expiresAt),
                    cancellationToken).ConfigureAwait(false);
            return renewed > 0;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var deleted = await db.PendingOccurrences
                .Where(p => p.Id == id && p.LeaseToken == leaseToken)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            return deleted > 0;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(string id, string leaseToken, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var released = await db.PendingOccurrences
                .Where(p => p.Id == id && p.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.LeaseOwner, (string?)null)
                    .SetProperty(p => p.LeaseToken, (string?)null)
                    .SetProperty(p => p.ClaimedAtUtc, (DateTime?)null)
                    .SetProperty(p => p.LeaseExpiresAtUtc, (DateTime?)null),
                    cancellationToken).ConfigureAwait(false);
            return released > 0;
        }
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.PendingOccurrences.AsNoTracking()
                .CountAsync(p => p.JobId == jobId, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>EF Core dead-letter store.</summary>
public sealed class EfCoreDeadLetterStore : IDeadLetterStore
{
    private readonly IDbContextFactory<WorkerKitDbContext> _factory;
    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    public EfCoreDeadLetterStore(IDbContextFactory<WorkerKitDbContext> factory, TimeProvider time)
    {
        _factory = factory;
        _time = time;
    }

    /// <inheritdoc />
    public async Task AddAsync(DeadLetterRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            db.DeadLetters.Add(new JobDeadLetterEntity
            {
                Id = record.Id,
                JobId = record.JobId,
                ExecutionId = record.ExecutionId,
                Scope = record.Scope,
                ItemId = record.ItemId,
                FailureKind = record.FailureKind,
                Reason = record.Reason,
                AttemptCount = record.AttemptCount,
                PayloadSummary = record.PayloadSummary,
                CreatedAtUtc = record.CreatedAtUtc.UtcDateTime,
                ReprocessedAtUtc = record.ReprocessedAtUtc?.UtcDateTime,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetterRecord>> GetPendingAsync(string? jobId, int maxCount, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var query = db.DeadLetters.AsNoTracking().Where(d => d.ReprocessedAtUtc == null);
            if (jobId is not null)
            {
                query = query.Where(d => d.JobId == jobId);
            }

            var entities = await query.OrderBy(d => d.CreatedAtUtc).Take(maxCount)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            return entities.Select(e => new DeadLetterRecord
            {
                Id = e.Id,
                JobId = e.JobId,
                ExecutionId = e.ExecutionId,
                Scope = e.Scope,
                ItemId = e.ItemId,
                FailureKind = e.FailureKind,
                Reason = e.Reason,
                AttemptCount = e.AttemptCount,
                PayloadSummary = e.PayloadSummary,
                CreatedAtUtc = EfCoreJobExecutionStore.Utc(e.CreatedAtUtc),
                ReprocessedAtUtc = e.ReprocessedAtUtc is { } reprocessed
                    ? EfCoreJobExecutionStore.Utc(reprocessed)
                    : null,
            }).ToList();
        }
    }

    /// <inheritdoc />
    public async Task MarkReprocessedAsync(string id, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.DeadLetters.Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReprocessedAtUtc, now), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

# Checkpoints

A **checkpoint** is the durable answer to one question: *if this job dies right now, where should
the next execution continue?*

ResilientWorkerKit stores checkpoints but never writes them. **The job owns its checkpoint** — the
engine has no idea what "progress" means for your data, and only the job code knows when a batch
has truly and durably succeeded.

## Model

| Property | Value |
|---|---|
| Cardinality | **Exactly one checkpoint per job** (`IJobCheckpointStore` is keyed by `JobId`) |
| Format | An opaque JSON payload, serialized from a type you define |
| Written by | Job code only, via `context.Checkpoints.SaveAsync<T>(...)` |
| Lifetime | Survives attempts, executions, restarts and deployments until the job overwrites or clears it |
| Serializer | `WorkerKitOptions.JsonSerializerOptions` (plain `JsonSerializerOptions` defaults) |

The stored record is `JobCheckpoint`:

```csharp
public sealed record JobCheckpoint(
    string JobId,
    string PayloadJson,
    string? PayloadType,      // typeof(T).FullName — diagnostics only, never used to deserialize
    DateTimeOffset UpdatedAtUtc);
```

`PayloadType` is recorded for humans reading the table. Reads deserialize into whatever `T` the
job asks for; the stored type name is not consulted.

## API

```csharp
public interface IJobCheckpointAccessor
{
    Task<T?> GetAsync<T>(CancellationToken cancellationToken = default);
    Task SaveAsync<T>(T checkpoint, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
```

| Member | Behavior |
|---|---|
| `GetAsync<T>` | Returns `default(T)` (i.e. `null` for reference types) when no checkpoint was ever saved. Throws `JobConfigurationException` when the stored payload cannot be deserialized as `T`. Logs at Debug on success. |
| `SaveAsync<T>` | Creates or replaces the job's single checkpoint. Throws `ArgumentNullException` for a null checkpoint. Stamps `UpdatedAtUtc` from `TimeProvider`. Also publishes a short summary to the execution record and health snapshot. |
| `ClearAsync` | Deletes the row. The next `GetAsync<T>` returns `default` and the job starts from scratch. |

### Worked example

From `samples/ReservationReconciliation.Worker/Jobs/ReservationSyncJob.cs` — the canonical pattern:

```csharp
/// <summary>Durable checkpoint of the sync job: where to continue after a crash or restart.</summary>
public sealed record ReservationSyncCheckpoint(string? ContinuationToken, int PagesProcessed);

public sealed class ReservationSyncJob : IWorkerJob
{
    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        // 1. Resume from the last fully processed page (or start fresh).
        var checkpoint = await context.Checkpoints.GetAsync<ReservationSyncCheckpoint>(cancellationToken)
            ?? new ReservationSyncCheckpoint(null, 0);

        var continuationToken = checkpoint.ContinuationToken;
        var pagesProcessed = checkpoint.PagesProcessed;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _apiClient.GetReservationsAsync(continuationToken, cancellationToken);
            context.ReportProgress($"page {pagesProcessed + 1} ({page.Items.Count} items)");

            foreach (var reservation in page.Items)
            {
                // 2. Every side effect is guarded by an idempotency key (see idempotency.md).
                var key = $"reservation:{reservation.Id}:v{reservation.Version}";
                if (await context.Idempotency.TryAcquireAsync(key, cancellationToken)
                    != IdempotencyAcquireResult.Acquired)
                {
                    continue; // already processed — no second side effect
                }

                _ledger.Reconcile(reservation);
                await context.Idempotency.MarkCompletedAsync(key, cancellationToken);
            }

            pagesProcessed++;

            // 3. The page is fully processed — only now may the checkpoint advance.
            if (page.NextContinuationToken is null)
            {
                await context.Checkpoints.SaveAsync(
                    new ReservationSyncCheckpoint(null, 0), cancellationToken); // pass finished: reset
                return;
            }

            await context.Checkpoints.SaveAsync(
                new ReservationSyncCheckpoint(page.NextContinuationToken, pagesProcessed), cancellationToken);
            continuationToken = page.NextContinuationToken;
        }
    }
}
```

## Supported checkpoint shapes

The payload is *your* type. The kit imposes no schema beyond "must round-trip through
`System.Text.Json`". These are the shapes that cover almost every batch job:

| Shape | When to use | Example |
|---|---|---|
| Last processed id | Monotonic integer/ULID keys, `WHERE Id > @last ORDER BY Id` | `record SyncCheckpoint(long LastProcessedId);` |
| Page number | APIs paged by index | `record SyncCheckpoint(int PageNumber, int PageSize);` |
| Continuation token | APIs that hand back an opaque cursor | `record SyncCheckpoint(string? ContinuationToken, int PagesProcessed);` |
| Cursor (composite) | Keyset pagination over a sort key plus a tie-breaker | `record SyncCheckpoint(DateTimeOffset SortKey, long TieBreakerId);` |
| Timestamp watermark | "Everything modified since X" incremental pulls | `record SyncCheckpoint(DateTimeOffset LastModifiedUtc);` |
| Version watermark | Sources exposing a change/row version | `record SyncCheckpoint(long LastChangeVersion);` |
| Custom JSON state | Multi-phase jobs that need more than a position | see below |

```csharp
// Custom JSON state: a multi-phase reconciliation that must remember which phase it reached.
public sealed record ReconciliationCheckpoint(
    string Phase,                       // "fetch" | "match" | "settle"
    long LastProcessedId,
    int RetriedBatches,
    IReadOnlyList<string> PendingRegions);
```

Two practical rules for the payload type:

- **Keep it additive.** A deployed job reads checkpoints written by the previous version. Add
  optional members with defaults; do not rename or repurpose existing ones. A payload that no
  longer deserializes fails the execution as `Misconfigured` (see below).
- **Keep it small.** The first 160 characters of the JSON end up in execution history and health
  snapshots (see [Checkpoint summaries](#checkpoint-summaries-in-history-and-health)).

## The golden rule

> **Advance the checkpoint only after the batch has fully and durably succeeded.**

The exact ordering inside a batch iteration:

```text
process items (idempotency-guarded)  →  commit local changes  →  SaveAsync(checkpoint)
```

Why this order and no other:

- **Checkpoint before the work** would claim progress that has not happened. A crash then skips the
  batch entirely — silent data loss, the one failure mode that no retry can repair.
- **Checkpoint before the local commit** has the same effect: the commit may never land, but the
  next execution starts after it anyway.
- **Checkpoint after the commit** can at worst repeat a batch, and repetition is exactly what
  idempotency keys are designed to absorb.

The kit enforces this by omission: the engine never writes a checkpoint on its own, and a failed
attempt cannot retroactively advance one. A checkpoint saved by attempt 1 survives even when
attempt 2 throws — verified by
`tests/ResilientWorkerKit.UnitTests/Engine/CheckpointAndIdempotencyContextTests.cs`
(`FailedBatch_DoesNotAdvanceTheCheckpoint`): the stored payload still points at the last page that
actually succeeded, and nothing further.

## Atomicity

Each save is a **single-row upsert keyed by `JobId`**:

- EF Core store: `Find(jobId)` → insert or update that one row → `SaveChangesAsync`.
- In-memory store: one `ConcurrentDictionary` assignment.

"Atomic enough" means precisely this: a checkpoint write either lands in full or not at all. There
is no partially written checkpoint, no torn payload, and no state in which the checkpoint describes
half a batch.

What it explicitly does **not** mean: the checkpoint write is **not** part of your job's own
database transaction. The EF Core store resolves its own `WorkerKitDbContext` from an
`IDbContextFactory`, so `SaveAsync` commits separately from whatever your job committed a moment
earlier.

### Crash between the local commit and the checkpoint save

This window is real and the design accepts it:

1. Batch items are applied, your transaction commits — including the idempotency records written
   through `context.Idempotency`.
2. The process dies before `SaveAsync` runs.
3. The next execution reads the *older* checkpoint and **re-runs that batch**.
4. Every item in the batch hits `TryAcquireAsync` and comes back `AlreadyCompleted`, so no second
   side effect is produced. The batch re-runs; the *work* does not.

This is why checkpoints and idempotency are designed as a pair, and why neither is optional in a
job that has external side effects. The integration test
`tests/ResilientWorkerKit.IntegrationTests/EndToEndResumeTests.cs` exercises the whole path across
a real process restart: page 1 applied, checkpoint stopped at page 2, restart, resume at page 2,
and only the genuinely new item produced a side effect.

## No distributed transaction

**There is no distributed transaction between an external API and your local database, and the kit
does not pretend otherwise.** An HTTP call cannot be enlisted in a database transaction, and no
amount of framework code changes that.

The compensating design is reconciliation plus idempotency:

```text
acquire idempotency key
  → call the external API (carrying the same key as an Idempotency-Key header)
    → record completion + local changes (one local transaction)
      → save the checkpoint
```

Failure windows and their outcomes:

| Crash point | Effect on the next execution |
|---|---|
| After acquire, before the API call | The key is `Pending` for a *dead* execution; a later execution re-acquires it once the record expires (TTL) or after it is marked failed, and retries the call. |
| After the API call, before the local commit | The batch re-runs and the call is re-issued — with the **same** `Idempotency-Key`, so a conforming remote side deduplicates it. The local side has no record yet, so it applies the result exactly once. |
| After the local commit, before the checkpoint save | The batch re-runs and is fully suppressed locally by the completed idempotency records. |

The remote half of this contract is the `Idempotency-Key` header emitted by
`ResilientWorkerKit.Http` — see [api-integration.md](api-integration.md) and
[idempotency.md](idempotency.md).

Because the guarantee is *reconciliation*, not *transaction*, jobs should be written so that a
full re-run over already-seen data is harmless: read the current state, compute the difference,
apply what is missing.

## Corrupted checkpoints

A payload that cannot be deserialized as the requested type is **never silently treated as "no
checkpoint"** — silently restarting from scratch would re-process everything, or (worse) look like
success while the real position was lost.

`GetAsync<T>` catches `JsonException` and rethrows:

```csharp
throw new JobConfigurationException(
    $"The stored checkpoint of job '{_jobId}' could not be deserialized as {typeof(T).Name}. " +
    "Clear the checkpoint or fix the checkpoint type.", ex);
```

`JobConfigurationException` implements `IJobFailureHint` with `FailureKind => Misconfigured`, so:

- the execution fails immediately with status `Failed` and `FailureKind = Misconfigured`;
- **it is not retried** — retrying a corrupted payload cannot help;
- the error message names the checkpoint, so the operator knows what to fix.

Verified by `CorruptedCheckpoint_FailsAsMisconfigured_NotSilently` in
`tests/ResilientWorkerKit.UnitTests/Engine/CheckpointAndIdempotencyContextTests.cs`.

Recovery is a deliberate operator decision: either deploy a payload type that can read the stored
JSON, or clear the checkpoint (`ClearAsync`, or delete the row from `WorkerKitCheckpoints`) and
accept a full re-run — which idempotency records will largely suppress anyway.

## Security

**Never put secrets or personal data in a checkpoint.** The payload is stored as plain JSON in
`WorkerKitCheckpoints.PayloadJson`, and a prefix of it is copied into execution history and health
snapshots, which are typically exposed to operators, log pipelines and health endpoints.

| Do not store | Store instead |
|---|---|
| API keys, tokens, connection strings | Nothing — resolve credentials from configuration at run time |
| Names, e-mail addresses, phone numbers, addresses | The opaque identifier (`reservation:41`) |
| Raw API response bodies | The cursor or continuation token that would fetch them again |
| Anything you would not want in a log line | A position, a count, a watermark |

A checkpoint should be a *position*, not a *payload*. If it grows into a payload, that state
belongs in your own domain tables.

## Checkpoint summaries in history and health

Every successful `SaveAsync<T>` produces a short summary:

```csharp
// type name + the first 160 characters of the JSON, ellipsized
$"{typeof(T).Name} {json.Length <= 160 ? json : json[..160] + "…"}"
```

That summary is:

- logged at **Debug** (`Checkpoint saved: {CheckpointSummary}`);
- written to `JobExecutionRecord.LastCheckpointSummary` (column capped at 500 characters);
- published to `JobHealthSnapshot.LastCheckpointSummary`, visible through
  `IJobHealthTracker` and the health-check adapter.

So an operator can answer "how far did the last run get?" without opening the checkpoint table —
which is also why the 160-character prefix must be safe to display. Verified by
`Checkpoint_SummaryLandsInExecutionRecordAndHealth`.

For live progress *within* an execution use `context.ReportProgress("page 3/10")`, which feeds
`JobHealthSnapshot.LastProgress`. Progress notes are in-memory only and are cleared when the next
execution starts; checkpoints are the durable half.

## Related

- [execution-semantics.md](execution-semantics.md) — at-least-once, execution identity, retries
- [idempotency.md](idempotency.md) — the other half of the pair
- [failure-handling.md](failure-handling.md) — what `Misconfigured` means and how failures classify

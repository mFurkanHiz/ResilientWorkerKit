# Idempotency

## Why it is mandatory, not optional

ResilientWorkerKit guarantees **at-least-once** execution and nothing stronger — see
[execution-semantics.md](execution-semantics.md). A job body can legitimately run more than once
over the same data:

- a transient failure retries the attempt (same `ExecutionId`, next `AttemptNumber`);
- a crash between a local commit and the checkpoint save re-runs the last batch;
- a restart resumes from the last checkpoint, which by design points *before* the work in flight;
- a misfire or manual trigger can replay an occurrence.

Every one of those paths is desirable — they are how work survives failure. What must not survive
is the **duplicate side effect**: the second charge, the second e-mail, the second ledger entry.

That is what idempotency records buy: the job re-runs, the side effect does not. Together with
durable checkpoints this produces *effectively-once side effects* in the normal case, with
well-defined behavior in every failure case.

## Key design

An idempotency key is a **stable business identity**. Stable means: the same logical unit of work
produces the exact same string, on every attempt, in every execution, on every host, forever.

The recommended pattern is `entity:id:version`:

```csharp
var key = $"reservation:{reservation.Id}:v{reservation.Version}";
```

| Segment | Purpose |
|---|---|
| `entity` | Namespaces the key so unrelated item types cannot collide |
| `id` | The stable identifier of the item in the source system |
| `version` | Lets a *legitimately changed* item be processed again while an unchanged replay is suppressed |

Keys are scoped per job — the store keys records by `(JobId, Key)` — so two jobs may use the same
key text without interfering. The key column is `nvarchar(400)` in the EF Core model.

### What must never go into a key

| Never | Why | Use instead |
|---|---|---|
| Personal data (names, e-mail, phone, addresses) | Keys are stored in plain text, appear in Debug logs (`Idempotent item skipped (key=…)`) and are read by operators | The opaque entity id |
| Secrets, tokens, signatures | Same exposure, and they rotate | Nothing |
| Timestamps that change per attempt (`DateTime.UtcNow`, elapsed times) | The key would be different on every retry, so **nothing is ever suppressed** — the feature silently stops working | The item's own version, ETag or last-modified value from the source |
| Random values (`Guid.NewGuid()`) | Same failure mode: a fresh key every attempt | A derived, deterministic identity |
| Attempt or execution numbers | Retries of the same execution must reuse the same key | Omit them |

A timestamp *is* acceptable when it is a property of the item, not of the run — e.g.
`reservation:41:2026-08-01T09:00:00Z` where that instant is the item's `LastModifiedUtc`.

## API

```csharp
public interface IJobIdempotencyAccessor
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<IdempotencyAcquireResult> TryAcquireAsync(string key, CancellationToken ct = default);
    Task MarkCompletedAsync(string key, CancellationToken ct = default);
    Task MarkFailedAsync(string key, CancellationToken ct = default);
}

public enum IdempotencyAcquireResult
{
    Acquired = 0,             // you own the side effect — do the work, then Complete or Fail
    AlreadyCompleted = 1,     // a live completed record exists — skip the side effect
    InProgressElsewhere = 2,  // another execution holds the key as Pending — skip for now
}
```

The accessor is bound to the current job and the current `ExecutionId`, and applies the job's
configured `WithIdempotencyTimeToLive(...)` automatically. All four methods throw
`ArgumentException` for a null/empty/whitespace key.

| Member | Behavior |
|---|---|
| `ExistsAsync` | `true` only for a **live, completed** record (a `Pending`/`Failed`/expired record reads as `false`). Logs at Debug when it returns `true`. |
| `TryAcquireAsync` | Atomically acquires the key for this execution; exactly one concurrent caller can win. Computes `ExpiresAtUtc = now + TTL` when a TTL is configured. Logs at Debug on `AlreadyCompleted`. |
| `MarkCompletedAsync` | Marks the key `Completed` and stamps `CompletedAtUtc`. Call **after** the side effect durably succeeded. No-op if the record is gone. |
| `MarkFailedAsync` | Marks the key `Failed` so a later execution may re-acquire and retry it. No-op if the record is gone. |

### Usage

```csharp
foreach (var reservation in page.Items)
{
    var key = $"reservation:{reservation.Id}:v{reservation.Version}";

    var acquire = await context.Idempotency.TryAcquireAsync(key, cancellationToken);
    if (acquire != IdempotencyAcquireResult.Acquired)
    {
        // AlreadyCompleted → a previous run did it. InProgressElsewhere → someone else owns it.
        context.Logger.LogInformation(
            "Reservation {Id} v{Version} skipped ({AcquireResult})",
            reservation.Id, reservation.Version, acquire);
        continue;
    }

    try
    {
        if (reservation.Nights < 0)
        {
            // Permanently invalid: quarantine the item, mark the key handled so it is not
            // retried forever, and keep processing the rest of the batch.
            await context.DeadLetters.AddAsync(
                $"reservation:{reservation.Id}",
                $"Invalid payload: nights={reservation.Nights}",
                payloadSummary: $"version={reservation.Version}, status={reservation.Status}",
                cancellationToken);
            await context.Idempotency.MarkCompletedAsync(key, cancellationToken);
            continue;
        }

        _ledger.Reconcile(reservation);                                   // the side effect
        await context.Idempotency.MarkCompletedAsync(key, cancellationToken);
    }
    catch (Exception)
    {
        // Release the key so a later execution may retry this item.
        await context.Idempotency.MarkFailedAsync(key, cancellationToken);
        throw;
    }
}
```

Two habits worth copying from the sample
(`samples/ReservationReconciliation.Worker/Jobs/ReservationSyncJob.cs`):

- **Mark a dead-lettered item `Completed`, not `Failed`.** It has been handled — quarantined
  deliberately — and re-acquiring it every run would just re-quarantine it forever.
- **Never leave an acquired key un-marked on the error path.** A `Pending` record blocks other
  executions until it expires; if the job has no TTL, it blocks them permanently.

## Record lifecycle

```text
              TryAcquireAsync
(absent) ─────────────────────► Pending ──── MarkCompletedAsync ───► Completed
                                   │                                     │
                                   └──── MarkFailedAsync ───► Failed     │ TTL elapses
                                                 │                       ▼
                                                 └──────────────► (behaves as absent)
```

`IdempotencyRecord` fields: `JobId`, `Key`, `Status`, `ExecutionId` (the execution that most
recently acquired it), `CreatedAtUtc`, `CompletedAtUtc`, `ExpiresAtUtc`.

### Expiry

`WithIdempotencyTimeToLive(TimeSpan)` on the job registration sets the TTL applied to every record
that job creates. The builder rejects a non-positive TTL with `JobConfigurationException`. With no
TTL configured, `ExpiresAtUtc` is `null` and records never expire.

```csharp
kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
    .WithInterval(TimeSpan.FromMinutes(5))
    .WithIdempotencyTimeToLive(TimeSpan.FromDays(7)));
```

Expiry is **logical, not physical**: an expired record still exists as a row (`GetAsync` still
returns it), but every decision treats it as absent — `ExistsCompletedAsync` returns `false` and
`TryAcquireAsync` re-acquires it in place.

### Re-acquisition rules

| Existing record | `TryAcquireAsync` result |
|---|---|
| None | `Acquired` (a fresh `Pending` record is inserted) |
| `Completed`, not expired | `AlreadyCompleted` |
| `Pending`, not expired, **owned by another execution** | `InProgressElsewhere` |
| `Pending`, not expired, **owned by the same `ExecutionId`** | `Acquired` |
| `Failed` | `Acquired` (the record is re-acquired in place: status back to `Pending`, new owner) |
| Expired (any status) | `Acquired` (same re-acquisition path) |

All six rows are covered by tests in
`tests/ResilientWorkerKit.UnitTests/Stores/InMemoryStoreTests.cs` and
`tests/ResilientWorkerKit.IntegrationTests/EfCorePersistenceTests.cs`
(`CompletedRecord_YieldsAlreadyCompleted`, `FailedRecord_CanBeReacquired`,
`ExpiredRecord_IsReusable`, `SameExecution_ReacquiringItsOwnPendingKey_IsAcquired`,
`IdempotencyLifecycle_CompletedThenExpired`).

## The interaction with retries

The fourth row above is the one that makes retries work.

`ExecutionId` is **stable across the retry attempts of one execution**; only `AttemptNumber`
changes. So when attempt 1 acquires `reservation:41:v7`, crashes before marking it, and attempt 2
re-acquires the same key, the store sees a `Pending` record whose `ExecutionId` matches the caller
and returns `Acquired` — the retry can finish the work it started.

Without that rule, an execution would deadlock against itself: attempt 2 would see
`InProgressElsewhere` for a key held by attempt 1 of the *same* execution, skip the item, and the
batch would silently lose work while reporting success.

A *different* execution asking for the same pending key still gets `InProgressElsewhere`, which is
the correct answer: someone else may still be working on it.

## Race conditions

`TryAcquireAsync` must be atomic — when two callers race for the same key, exactly one may win.
Both shipped stores settle the race in their storage layer, never with an application-level lock.

### In-memory store

`InMemoryIdempotencyStore` is a `ConcurrentDictionary<(JobId, Key), IdempotencyRecord>`:

- The winner is decided by a single **atomic `TryAdd`**. Exactly one caller inserts; every other
  caller re-reads the existing record and evaluates the rules above.
- Re-acquiring a `Failed`/expired record uses **`TryUpdate` with the previously read record as the
  comparand** (a compare-and-swap). The loser of that CAS loops and re-evaluates rather than
  overwriting.
- If the record is removed concurrently between the failed `TryAdd` and the read, the loop retries
  the insert.

`ConcurrentAcquires_ExactlyOneWinner` fires 32 concurrent acquisitions at one key and asserts
1 × `Acquired`, 31 × `InProgressElsewhere`.

### EF Core store

`EfCoreIdempotencyStore` pushes the decision to the database — no application locking:

- **The composite primary key `(JobId, Key)` *is* the guarantee.** Of two concurrent inserts for
  the same key, one succeeds and the other fails with `DbUpdateException`; the loser re-reads and
  re-evaluates. From `WorkerKitDbContext`:

  ```csharp
  // The composite primary key IS the idempotency guarantee: of two concurrent
  // inserts for the same (JobId, Key), exactly one succeeds at the database.
  entity.HasKey(e => new { e.JobId, e.Key });
  entity.Property(e => e.Version).IsConcurrencyToken();
  ```

- **Re-acquiring a `Failed`/expired record is guarded by the `Version` concurrency token.** The
  update increments `Version` and is written with the original value in the `WHERE` clause, so of
  two concurrent re-acquires one affects a row and the other throws
  `DbUpdateConcurrencyException`; the loser re-reads and re-evaluates.

- The re-read loop runs **at most three iterations**. If the key is still contested after that,
  the method returns `InProgressElsewhere` — a deliberately conservative answer: skip the item now
  rather than risk a duplicate side effect. The item is picked up by a later execution.

`ConcurrentIdempotencyAcquires_ExactlyOneWinner_SettledByTheDatabase` fires 16 concurrent
acquisitions against a real SQLite database and asserts 1 × `Acquired`,
15 × `InProgressElsewhere`.

Records are durable, so the suppression survives a restart:
`IdempotencyRecords_SurviveRestart_AndPreventDuplicateSideEffects` runs the same page of items
through two host lifetimes and asserts the second host produces **zero** side effects.

> The in-memory stores are for tests and demos only. Use
> `ResilientWorkerKit.EntityFrameworkCore` in production — an idempotency record that dies with
> the process protects nothing across the restart it exists to survive.

## Propagating idempotency to the remote side

Local records stop *your* job from repeating a side effect. They cannot stop the *remote* system
from applying a request twice when your process dies after sending it but before recording the
outcome. That half of the contract is the `Idempotency-Key` header.

`ResilientWorkerKit.Http` ships `IdempotencyKeyHandler`:

- Applies to `POST`, `PUT` and `PATCH` requests that do not already carry the header.
- Enabled by `ResilientApiClientOptions.EnableIdempotencyKey` (default `false`); header name is
  `IdempotencyKeyHeaderName`, default `Idempotency-Key`.
- Uses the key you attach to the request; falls back to a fresh `Guid` when none was set.
- Sits **outside** the resilience handler, so every HTTP-level retry of one request carries the
  **same** key and the server can deduplicate replays.

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, "reservations")
    .WithIdempotencyKey($"reservation:{reservation.Id}:v{reservation.Version}");
```

Use the *same* string as your local idempotency key: one identity, both sides. See
[api-integration.md](api-integration.md).

## Operational guidance

### Choosing a TTL

The TTL answers one question: *how long must a replay be suppressed?* It should comfortably exceed
the longest window in which the same item can legitimately come back:

| Job shape | Reasonable TTL |
|---|---|
| Frequent incremental sync (minutes), source re-emits recent items | Hours to a few days |
| Daily/weekly reconciliation over an overlapping window | Longer than the overlap, e.g. 30 days |
| Monthly settlement where a replay must never re-apply | No TTL (`null`) — keep the record forever |

Two failure modes to weigh:

- **TTL too short** — an expired record is re-acquirable, so a late replay *does* produce a second
  side effect. The suppression window must outlive the replay window.
- **TTL too long / absent** — records accumulate. This is usually the safer error, and it is a
  storage problem rather than a correctness problem.

Also remember that a `Pending` record from a crashed execution blocks other executions until it
expires. With no TTL, that block is permanent unless the record is removed, which is a strong
argument for setting *some* TTL on jobs whose keys are re-derivable.

### Cleanup and storage growth

**The kit ships no automatic cleanup.** Expiry is logical only — expired rows stay in
`WorkerKitIdempotencyRecords` until something deletes them. What is provided:

- `IIdempotencyStore.RemoveAsync(jobId, key)` — deletes one record.
- An index on `ExpiresAtUtc`, so a bulk delete of expired rows is cheap.

Plan for growth explicitly. Two workable options:

1. **A maintenance job.** Register an ordinary `IWorkerJob` on a daily/weekly schedule that deletes
   expired records (and completed records older than your retention window) directly through your
   own `DbContext` or a SQL command.
2. **Database-side retention.** A scheduled agent job / cron task that prunes the table.

Rough sizing: one row per processed item per version. A job reconciling 100k items a day with a
7-day TTL keeps on the order of 700k rows before pruning — small, but unbounded without a policy.

The same reasoning applies to `WorkerKitExecutions` and `WorkerKitDeadLetters`, which also grow
without a retention policy.

### Monitoring

- Debug log `Idempotent item skipped (key={IdempotencyKey})` shows suppression happening. A sudden
  drop to zero suppressions on a job that used to suppress often is a strong signal that key
  derivation changed (a version field started moving, or a timestamp leaked into the key).
- A rising count of `Pending` records that never reach `Completed` indicates executions dying
  mid-item, or an error path that forgets `MarkFailedAsync`.

## Related

- [checkpoints.md](checkpoints.md) — the other half of the pair
- [execution-semantics.md](execution-semantics.md) — at-least-once, execution identity
- [failure-handling.md](failure-handling.md) — dead letters for items that can never succeed
- [api-integration.md](api-integration.md) — the `Idempotency-Key` header and HTTP resilience

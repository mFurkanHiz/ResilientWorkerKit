# Execution Semantics

## The guarantee: at-least-once + durable checkpoints + idempotency

ResilientWorkerKit **does not and cannot provide exactly-once execution**. No system that calls
external APIs and writes to a local database can, without distributed transactions that the
external side would have to participate in.

What the kit provides instead is the practical, honest contract:

1. **At-least-once**: a schedule occurrence that is due will be executed at least once (subject to
   the configured misfire policy). Crashes, retries and restarts may cause the *job body* to run
   again over data it has already seen.
2. **Durable checkpoints**: the job records how far it got (`context.Checkpoints`). After a crash
   or failure, the next execution resumes from the last checkpoint that was saved after a fully
   successful batch — it does not start over.
3. **Idempotent processing**: for each side-effecting item the job derives a stable idempotency
   key (`entity:id:version`) and consults `context.Idempotency`. A record that was already
   completed is skipped, so re-delivery does not produce a second side effect.

Together these give *effectively-once side effects* in the normal case, with well-defined
behavior in every failure case.

## Execution identity

| Concept | Lifetime | Example |
|---|---|---|
| `JobId` | Forever | `monthly-billing` |
| `ScheduledExecutionId` | One schedule occurrence | `monthly-billing:2026-08` |
| `ExecutionId` | One execution incl. all its retry attempts | `01J8...` (ULID-like) |
| `AttemptNumber` | One attempt | `2` |

Rules:

- Retrying after a transient failure **keeps the same `ExecutionId`** and increments
  `AttemptNumber`.
- A new schedule occurrence **always creates a new `ExecutionId`** and a new
  `ScheduledExecutionId`.
- Calendar-based schedules produce **deterministic** `ScheduledExecutionId`s derived from the
  scheduled *local* time (monthly: year+month). Before an occurrence runs, the engine asks the
  execution store whether that identity has already **completed** — this is what prevents a
  monthly job from running twice in one month across host restarts, a one-time job from
  re-firing, and a DST fall-back hour from double-firing.
- Misfire recovery additionally checks whether **any** execution record exists for the identity,
  so a restart never *creates* the same missed occurrence twice.

## Execution states

`Running → Completed | Failed | Cancelled | TimedOut | Abandoned`

| State | Meaning |
|---|---|
| `Completed` | Job body returned normally |
| `Failed` | Exhausted retries, or permanent/misconfigured failure |
| `Cancelled` | Cooperative cancellation observed (host shutdown or manual) — logged as Information, not error |
| `TimedOut` | Total execution timeout elapsed (attempt timeouts surface as transient failures first) |
| `Abandoned` | Record was still `Running` when a new host started ⇒ the previous process died mid-run |

`Abandoned` marking happens during startup recovery. Since v0.1 assumes a single active host
instance, any `Running` record found at startup belongs to a dead process. (Multi-instance
deployments need Phase 2's distributed locking; see [limitations.md](limitations.md).)

## Failure classification

`JobFailureKind`: `Transient`, `Permanent`, `Cancelled`, `TimedOut`, `Abandoned`, `Misconfigured`.

The default classifier resolves, in order:

1. `OperationCanceledException` when the shutdown/manual token fired → `Cancelled`
2. `OperationCanceledException`/`TimeoutException` when the timeout token fired → `TimedOut` (total) or `Transient` (attempt)
3. Any exception implementing `IJobFailureHint` → the hint's kind (+ optional `RetryAfter`)
4. `JobConfigurationException` → `Misconfigured` (never retried)
5. `TimeoutException` → `Transient`
6. `HttpRequestException` with a status code → `Transient` for 408/429/5xx, `Permanent` for other 4xx
7. Everything else → `Transient` (a conservative default: a retry is cheap, and permanent
   failures burn at most `MaxRetries` attempts before landing in `Failed`)

Only `Transient` failures are retried.

## Retry

- Exponential backoff: `BaseDelay × Multiplier^(attempt-1)`, capped at `MaxDelay`, with
  proportional jitter (`±JitterFactor`).
- A `RetryAfter` hint (e.g. from HTTP 429) **overrides** the computed backoff for that attempt.
- `MaxRetries` = number of retries after the first attempt (`MaxRetries = 3` ⇒ up to 4 attempts).
- Attempt timeout (`JobRetryOptions.AttemptTimeout`) cancels a single attempt → classified
  `Transient` → retried. Total timeout (`WithTimeout`) cancels the whole execution → `TimedOut`.
- Each attempt is logged individually; the execution record carries the final `AttemptCount`.
- When retries are exhausted: the execution is recorded `Failed`, an execution-level dead letter
  is written if `DeadLetterOnExhaustedRetries()` was configured, the health tracker increments
  consecutive failures — **and the host, the scheduler loop and every other job continue
  untouched**. The job runs again at its next scheduled occurrence.

## Checkpoints and the transaction boundary

The engine never writes checkpoints on its own; only job code decides when progress is real:

```text
fetch page → process items (idempotency-guarded) → commit local changes → SaveAsync(checkpoint)
```

- Save the checkpoint **after** the batch's database work committed. If the process dies between
  the commit and the checkpoint save, the batch re-runs — and the idempotency records written in
  the same local transaction suppress the duplicate side effects. This is why checkpoints and
  idempotency are designed as a pair.
- There is **no distributed transaction** between an external API call and your local database,
  and the kit does not pretend otherwise. The documented pattern is:
  acquire idempotency key → call external API → record completion + local changes → checkpoint.
  A crash between the external call and the completion record causes one re-delivery attempt
  whose duplicate is suppressed on the *local* side; the external side should receive the same
  `Idempotency-Key` header so it can deduplicate too (see [api-integration.md](api-integration.md)).
- A checkpoint that fails to deserialize (corrupted payload) surfaces as `Misconfigured` with a
  clear error — it is never silently treated as "no checkpoint".

## Cancellation & graceful shutdown

On host shutdown:

1. Scheduler loops stop starting new occurrences immediately.
2. Running jobs get their `CancellationToken` signalled.
3. The engine waits up to `ShutdownGracePeriod` (default 30s) for running executions to finish.
4. An execution that completes inside the grace period is recorded normally (`Completed`/`Failed`).
5. An execution that observes cancellation is recorded `Cancelled` — at Information level.
   Shutdown is not an error and is never logged as one.
6. An execution that ignores the token past the grace period is left `Running`; the *next* startup
   marks it `Abandoned`. It is never marked successful.
7. Locks are released; the checkpoint state remains whatever the job last saved successfully.

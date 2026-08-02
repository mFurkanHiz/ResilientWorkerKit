# Failure Handling

Everything a job can do wrong ends in one of six categories, and each category has exactly one
consequence. This document describes what the engine actually does — the classification order, the
retry arithmetic, the timeout and cancellation semantics, dead lettering, and the isolation
invariants that keep one failing job from affecting anything else.

## Failure kinds

`JobFailureKind` has six values:

| Kind | Meaning | Retried? | Resulting execution status |
|---|---|---|---|
| `Transient` | A temporary condition — network blip, 5xx, dependency timeout, attempt timeout | **Yes**, up to `MaxRetries` | `Completed` if a retry succeeds, otherwise `Failed` |
| `Permanent` | A deterministic failure — validation error, unsupported payload, domain rule violation | No | `Failed` |
| `Cancelled` | Cooperative cancellation observed (host shutdown or manual stop). **Not an error** | No | `Cancelled` |
| `TimedOut` | The total execution timeout elapsed | No | `TimedOut` |
| `Abandoned` | The record was still `Running` when a new host started — the owning process died | No | `Abandoned` (set by startup recovery, never by the runner) |
| `Misconfigured` | The job or its configuration is invalid — unknown time zone, corrupted checkpoint, invalid schedule | No | `Failed` |

Only `Transient` is retried. Everything else ends the execution on the attempt that produced it.

`Abandoned` is the one kind the runner never assigns: it is written by startup recovery
(`IJobExecutionStore.MarkRunningAsAbandonedAsync`) when a `Running` record outlives its process.

## Default classification order

`DefaultJobFailureClassifier.Classify` resolves in this order — the first match wins:

| # | Condition | Result |
|---|---|---|
| 1 | The exception implements `IJobFailureHint` | The hint's `FailureKind` **and** its `RetryAfter` |
| 2 | `OperationCanceledException` | `Cancelled` |
| 3 | `HttpRequestException` **with** a `StatusCode` | 408, 429, ≥ 500 → `Transient`; other 4xx → `Permanent`; anything else → `Transient` |
| 4 | `HttpRequestException` **without** a status code (DNS, socket, TLS failures) | `Transient` |
| 5 | `TimeoutException` | `Transient` |
| 6 | Everything else | `Transient` |

Rule 1 is the extension point: `TransientJobException`, `PermanentJobException`,
`JobConfigurationException` and `ResilientWorkerKit.Http`'s `ApiRequestException` all implement
`IJobFailureHint`, which is how other packages and user code steer the retry decision without the
core taking a dependency on them.

### The runner decides cancellation and timeouts *before* the classifier

`JobRunner` interprets cancellation itself, in this order, and only calls the classifier when none
of these matched:

1. `OperationCanceledException` **and** the host/manual stopping token fired → `Cancelled`, no retry.
2. `OperationCanceledException` **and** the total-timeout token fired → `TimedOut`, no retry.
3. `OperationCanceledException` **and** the attempt-timeout token fired → hard-coded `Transient`,
   retry-eligible. The classifier is never consulted, so rule 2 above cannot turn an attempt
   timeout into `Cancelled`.
4. Anything else → `IJobFailureClassifier.Classify(ex)`.

This is why rule 2 in the classifier table is safe: by the time it can fire, the runner has already
established that the cancellation was not caused by a timeout.

### Why unknown exceptions default to `Transient`

Rule 6 is deliberate and conservative. The asymmetry between the two possible mistakes:

- Retrying a failure that was actually permanent costs at most `MaxRetries` extra attempts. The
  execution then lands in `Failed` exactly as it would have anyway.
- *Not* retrying a failure that was actually transient loses the work until the next scheduled
  occurrence — sometimes a month away.

So the default takes the cheap mistake. Deterministic failures should say so explicitly by throwing
`PermanentJobException` (or any `IJobFailureHint`), rather than relying on the engine to guess.

A classifier that itself throws is caught: the failure is logged as `ClassifyFailure` and the
exception is treated as `Transient`.

## Influencing classification

### `TransientJobException` — retry this

```csharp
if (!response.IsSuccessStatusCode)
{
    throw new TransientJobException(
        $"Upstream returned {(int)response.StatusCode} for /reservations",
        retryAfter: TimeSpan.FromSeconds(30));   // optional; overrides the computed backoff
}
```

### `PermanentJobException` — never retry this

```csharp
if (reservation.Nights < 0)
{
    throw new PermanentJobException($"Reservation {reservation.Id} has negative nights");
}
```

### `JobConfigurationException` — this is a configuration bug

Classified `Misconfigured` and never retried. Thrown by the kit during startup validation (unknown
time zone, invalid misfire policy, `MaxRetries < 0`, …) and at run time by
`IJobCheckpointAccessor.GetAsync<T>` when the stored checkpoint cannot be deserialized. Throw it
yourself when a job discovers that its own configuration is impossible.

### `IJobFailureHint` — teach your own exception types

```csharp
public sealed class PartnerApiException : Exception, IJobFailureHint
{
    public PartnerApiException(string message, int status, TimeSpan? retryAfter = null)
        : base(message)
    {
        FailureKind = status is 408 or 429 or >= 500 ? JobFailureKind.Transient : JobFailureKind.Permanent;
        RetryAfter = retryAfter;
    }

    public JobFailureKind FailureKind { get; }
    public TimeSpan? RetryAfter { get; }
}
```

This is exactly how `ResilientWorkerKit.Http.ApiRequestException` works: it carries a safe message
(no bodies, no query strings, no secrets), maps the status code, and propagates `Retry-After` into
the backoff.

### A custom `IJobFailureClassifier` — for types you do not own

```csharp
public sealed class DomainAwareClassifier : IJobFailureClassifier
{
    private readonly DefaultJobFailureClassifier _fallback = new();

    public JobFailureClassification Classify(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => JobFailureClassification.Transient,
        ValidationException          => JobFailureClassification.Permanent,
        SqlException { Number: 1205 } => JobFailureClassification.Transient,   // deadlock victim
        _                            => _fallback.Classify(exception),
    };
}

// Register BEFORE AddResilientWorkerKit — the engine registers its default with TryAddSingleton,
// so an existing registration is left untouched.
services.AddSingleton<IJobFailureClassifier, DomainAwareClassifier>();
services.AddResilientWorkerKit(kit => { /* ... */ });
```

The classifier is host-wide (one registration for all jobs). Delegating to
`DefaultJobFailureClassifier` for unmatched exceptions preserves the `IJobFailureHint` handling.

## Retry

### `MaxRetries` semantics

`MaxRetries` counts **retries after the first attempt**:

| `MaxRetries` | Total attempts |
|---|---|
| 0 | 1 (no retries) |
| 1 | 2 |
| 3 (default) | 4 |

The runner retries while `classification.Kind == Transient && attempt <= MaxRetries`.
`RetriesExhausted_ExecutionFails_WithTransientKind` asserts 3 attempts for `MaxRetries = 2`.

### Backoff

```text
delay = BaseDelay × BackoffMultiplier ^ (retryNumber − 1)      // retryNumber is 1-based
delay = min(delay, MaxDelay)                                    // cap applied BEFORE jitter
delay = delay × (1 − JitterFactor + 2 × JitterFactor × sample)  // sample uniform in [0,1)
delay = clamp(delay, 0, MaxDelay × (1 + JitterFactor))
```

Defaults (`JobRetryOptions`):

| Option | Default | Validation (`JobConfigurationException` on violation) |
|---|---|---|
| `MaxRetries` | 3 | ≥ 0 |
| `BaseDelay` | 2 seconds | ≥ 0 |
| `MaxDelay` | 1 minute | ≥ 0 |
| `BackoffMultiplier` | 2.0 | ≥ 1 |
| `JitterFactor` | 0.2 | in [0, 1) |
| `AttemptTimeout` | `null` (no per-attempt limit) | > 0 when set |

Worked example with `BaseDelay = 2s`, `BackoffMultiplier = 2`, `MaxDelay = 30s` (the configuration
used in `tests/ResilientWorkerKit.UnitTests/Engine/RetryDelayCalculatorTests.cs`):

| Retry # | Raw backoff | After the `MaxDelay` cap | With ±20% jitter |
|---|---|---|---|
| 1 | 2s | 2s | 1.6s – 2.4s |
| 2 | 4s | 4s | 3.2s – 4.8s |
| 3 | 8s | 8s | 6.4s – 9.6s |
| 4 | 16s | 16s | 12.8s – 19.2s |
| 5 | 32s | **30s** (capped) | 24s – 36s |

### Jitter

The multiplier is uniform in `[1 − JitterFactor, 1 + JitterFactor]`; the engine draws the sample
from `Random.Shared.NextDouble()`. With the default `JitterFactor = 0.2` every delay is scaled by
0.8 – 1.2. `Jitter_ExtremeSamples_HitTheBounds` pins the endpoints for a 2s base: 1.6s and 2.4s.

Because jitter is applied *after* the cap and the final clamp allows `MaxDelay × (1 + JitterFactor)`,
the true worst-case delay is `MaxDelay × 1.2` with default jitter — 36s in the table above, not 30s.
Set `JitterFactor = 0` to make delays exactly deterministic.

Jitter exists so that N jobs (or N hosts) that failed against the same dependency at the same
moment do not retry in lockstep and re-create the outage they are waiting out.

### `Retry-After` override

When the classification carries a `RetryAfter` — from an `IJobFailureHint` exception, e.g. an HTTP
`Retry-After` header parsed by `ResilientWorkerKit.Http` — `RetryDelayCalculator` returns that
value directly and skips the backoff computation entirely. A negative hint is clamped to zero.

```csharp
throw new TransientJobException("throttled", retryAfter: TimeSpan.FromSeconds(45));
// → the next attempt starts in 45s, regardless of the computed backoff
```

Note: the hint **always** wins, including when it is *shorter* than the computed backoff. (The XML
comments on `JobFailureClassification.RetryAfter` and `IJobFailureHint.RetryAfter` describe it as a
minimum / "overrides when longer"; the implementation in `RetryDelayCalculator.Compute` overrides
unconditionally.)

### What happens between attempts

- The failure is logged at **Warning**: `Transient failure on attempt {N}; retry {R}/{Max} in {Delay}`.
- The execution record is updated with the new `AttemptCount`.
- The engine waits using `TimeProvider`-based delay, so the wait is fully controllable in tests.
- Cancellation *during* the delay ends the execution: `Cancelled` if the host is stopping,
  `TimedOut` if the total timeout elapsed (`CancellationDuringRetryDelay_ProducesCancelled`).
- Each attempt gets a **fresh DI scope and a fresh job instance**. `ExecutionId`,
  `context.Items` and the accessors stay the same; only `AttemptNumber` changes.
- A retry that succeeds is logged at Information: `Retry succeeded on attempt {N}`.

## Attempt timeout vs. total timeout

Two independent limits, two different outcomes:

| | Attempt timeout | Total timeout |
|---|---|---|
| Configured with | `WithRetry(r => r.AttemptTimeout = ...)` | `WithTimeout(...)` on the job |
| Covers | One attempt | The whole execution, all attempts and backoff delays included |
| On expiry | Attempt is cancelled, classified **`Transient`**, **retried** | Execution is cancelled and ends |
| Final status | `Completed` if a later attempt succeeds, otherwise `Failed` (kind `Transient`) | `TimedOut` (kind `TimedOut`) |
| Log | Warning per attempt, then Error if retries exhaust | Error: `Execution timed out after {Duration} ms (limit {Timeout})` |

```csharp
kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
    .WithInterval(TimeSpan.FromMinutes(5))
    .WithTimeout(TimeSpan.FromMinutes(2))          // total: the whole execution
    .WithRetry(r =>
    {
        r.MaxRetries = 3;
        r.AttemptTimeout = TimeSpan.FromSeconds(30); // per attempt: hung call → retry
    }));
```

`AttemptTimeout_RetriesTheAttempt_ThenFails` verifies the pairing: with `MaxRetries = 1` and an
80 ms attempt timeout, both attempts time out, the execution ends `Failed`, and the recorded
`FailureKind` is `Transient` — not `TimedOut`.

Both timeouts are validated as positive at registration; a non-positive value throws
`JobConfigurationException` at startup.

## Cancellation

Host shutdown and timeouts both surface as `OperationCanceledException`. The runner tells them
apart by asking **which token fired**, in this order:

1. The host/manual stopping token → `Cancelled`
2. The total-timeout token → `TimedOut`
3. The attempt-timeout token → `Transient` (retried)

Cancellation caused by shutdown is **not an error and is never logged as one**. It is logged at
**Information**:

> `Execution cancelled after {DurationMs} ms (host shutdown or manual stop); this is not an error`

Consequences for jobs:

- Honor the token. `cancellationToken.ThrowIfCancellationRequested()` between batches and pass the
  token to every async call is the whole contract.
- An execution that finishes inside `ShutdownGracePeriod` (default 30s) is recorded normally.
- An execution that ignores the token past the grace period is left `Running` and is marked
  `Abandoned` by the next startup. It is never marked successful.
- Checkpoint state remains whatever the job last saved successfully — see
  [checkpoints.md](checkpoints.md).

## When retries are exhausted

The transient failure on the final attempt produces:

1. Execution status `Failed`, `FailureKind = Transient`, `AttemptCount` = the number of attempts
   made, `ErrorType`/`ErrorMessage`/`ErrorDetail` recorded (message truncated to 500 characters,
   detail to 4000).
2. An **Error** log: `Retries exhausted after {AttemptCount} attempt(s); execution failed`.
3. An execution-level dead letter, **if** `DeadLetterOnFailure()` was configured.
4. The health tracker increments `ConsecutiveFailures` for that job.
5. **Nothing else.** The host keeps running, the scheduler loops keep running, every other job
   keeps running, and this job runs again at its next scheduled occurrence — resuming from its
   checkpoint.

Point 5 is verified end-to-end in
`tests/ResilientWorkerKit.IntegrationTests/EndToEndResumeTests.cs`: while the sync job exhausts its
retries against a 500-ing upstream, a second job continues completing normally on the same host,
and after a restart the sync job resumes from its checkpoint and finishes.

## Retry now vs. retry later

There are two retry mechanisms and they solve different problems. Mixing them up is the easiest
way to build something that looks reliable and is not.

| | `WithRetry(...)` | `RetryLater(...)` |
|---|---|---|
| Scope | Attempts **inside** one execution | A **new execution** after one failed for good |
| Typical delay | Seconds | Minutes to hours |
| `ExecutionId` | Same across attempts | New, linked to the origin occurrence |
| Where the wait lives | In memory, in the running execution | In the pending-occurrence store |
| Survives a process restart | **No** | **Yes** |
| Execution status while waiting | `Running` (holds the overlap lock) | Nothing is running |
| Good for | A transient blip: a dropped connection, a 503 | "This action must eventually happen" |

They compose. In-execution attempts are exhausted first; only then, if the execution ends
`Failed`, is a follow-up queued.

```csharp
kit.AddJob<OpenTicketSaleJob>("ticket-sale-open", job => job
    .AtLocalTimes("Europe/Istanbul", new DateTime(2026, 8, 15, 10, 0, 0))

    // Fast, in-memory: ride out a momentary upstream hiccup.
    .WithRetry(r => { r.MaxRetries = 2; r.BaseDelay = TimeSpan.FromSeconds(5); })

    // Slow, durable: if the sale did not open, try again every 5 minutes, up to 3 times,
    // even if the host is redeployed in between.
    .RetryLater(maxAttempts: 3, delay: TimeSpan.FromMinutes(5)));
```

### How a follow-up is planned

When an execution ends `Failed` and the job has a follow-up policy:

1. The engine computes `DelayFor(ordinal)` — `Delay × BackoffMultiplier^(ordinal-1)`, clamped to
   `MaxDelay`. With the defaults (multiplier 1) the follow-ups are evenly spaced.
2. It writes a `PendingOccurrence` due at `now + delay`, with identity
   `<origin identity>+followup-<n>` and `OriginScheduledExecutionId` pointing at the occurrence
   that first failed.
3. The scheduler loop treats the pending queue and the schedule as equals: whichever is due first
   is what runs next. On startup the queue is read from the store, which is why a follow-up
   queued by a process that no longer exists still runs.
4. Claiming an occurrence deletes its row, so exactly one runner can win it.
5. When the ordinal would exceed `MaxAttempts`, nothing more is queued and an **Error** is logged:
   *"Follow-up retries exhausted after N attempt(s)"*.

### Permanent failures

By default a `Permanent` or `Misconfigured` failure does **not** queue a follow-up: a
deterministic failure normally repeats, so retrying it only burns the window and the log. Opt in
when an operator is expected to fix the cause between attempts:

```csharp
job.RetryLater(o =>
{
    o.MaxAttempts = 6;
    o.Delay = TimeSpan.FromMinutes(30);
    o.RetryPermanentFailures = true;
});
```

> Follow-up retries are only as durable as the store behind them. The default in-memory store
> loses the queue with the process, which defeats the purpose — configure
> `UseEntityFrameworkCore(...)` (see [persistence.md](persistence.md)).

### What a follow-up does not disturb

- **It never moves the schedule.** An out-of-band retry cannot change when the next scheduled
  occurrence is due, so a failure in August cannot make a monthly job skip September.
- **It is never dropped by the overlap policy.** If the job is busy when a follow-up comes due,
  the occurrence stays queued and runs when capacity frees up. The overlap policy governs
  schedule occurrences — deciding whether a *recurring* run may be skipped — not planned actions
  that exist because they must happen.
- **It is never lost to a lock.** The queue row is claimed only once the engine has decided to
  run it, and returned to the queue if the job lock turns out to be unavailable.
- **Its identity stays bounded.** Each follow-up is identified as `<origin>+followup-<n>`,
  derived from the original occurrence rather than chained onto the previous retry.

## Dead letters

Two distinct mechanisms write to the same store. They are not interchangeable.

### Execution-level — the whole run failed

Opt in per job with `DeadLetterOnFailure()`. Written by the engine when an execution ends
`Failed`.

```csharp
kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
    .WithInterval(TimeSpan.FromMinutes(5))
    .DeadLetterOnFailure());
```

| Field | Value written by the engine |
|---|---|
| `Scope` | `"execution"` |
| `ItemId` | `null` |
| `FailureKind` | The final classification |
| `Reason` | The recorded (already truncated) error message, or the failure kind name, or `"unknown failure"` |
| `AttemptCount` | Number of attempts made |
| `PayloadSummary` | `null` |

Despite the method name, the record is written for **every** `Failed` execution when the flag is
set — including `Permanent` and `Misconfigured` failures that were never retried, not only
exhausted-retry failures. `Cancelled` and `TimedOut` executions do not produce one.

### Item-level — one item failed, the batch continued

Written by job code through `context.DeadLetters.AddAsync(...)`. This is the "poison message"
mechanism: quarantine the item that can never succeed, keep processing the rest.

```csharp
await context.DeadLetters.AddAsync(
    itemId: $"reservation:{reservation.Id}",                       // safe identifier
    reason: $"Invalid payload: nights={reservation.Nights}",       // sanitized description
    payloadSummary: $"version={reservation.Version}, status={reservation.Status}",
    cancellationToken);

// Mark the idempotency key completed: the item has been handled (deliberately quarantined),
// so it must not be re-acquired on every future run.
await context.Idempotency.MarkCompletedAsync(idempotencyKey, cancellationToken);
```

| Field | Value |
|---|---|
| `Scope` | `"item"` |
| `ItemId` | Your identifier (required, non-blank) |
| `Reason` | Your sanitized description (required, non-blank) |
| `PayloadSummary` | Optional masked summary |
| `FailureKind` | `null` |
| `AttemptCount` | `1` |

Both variants also get `Id`, `JobId`, `ExecutionId`, `CreatedAtUtc`, a Warning log
(`Dead letter created (scope=…, item=…): …`) and a metrics increment. `ReprocessedAtUtc` is set by
`IDeadLetterStore.MarkReprocessedAsync` when an operator or a reprocessing job handles the entry;
`GetPendingAsync` returns unprocessed records oldest first.

### Masking requirement

**`Reason` and `PayloadSummary` must be masked or summarized — never a raw payload.** Dead-letter
records are operator-facing: they are read from the database, surfaced in tooling, and often copied
into tickets.

| Do not write | Write |
|---|---|
| The raw API response body | `"Invalid payload: nights=-2"` |
| A customer name or e-mail | `"reservation:41"` |
| A URL with a token in the query string | The path and status: `"POST /reservations → 422"` |
| The full request JSON | `"version=7, status=Cancelled"` |

`ResilientWorkerKit.Http` follows the same rule for its own diagnostics — `ApiRequestException`
messages carry status and path but never bodies or query strings, which the end-to-end test asserts
explicitly.

## Failure isolation invariants

| Invariant | Enforcement point |
|---|---|
| A job exception can never reach the host | `JobRunner.RunCoreAsync` wraps every attempt in `catch (Exception ex)`; the runner's contract is "never throws" |
| A scheduler loop can never crash the host | `JobScheduleLoop.RunAsync` has a last-resort `catch (Exception)` that logs `SchedulerLoopCrashed` (event 1029) and exits that loop only |
| One job's failure never affects another job | One independent async loop per job in `WorkerKitHostedService`; `Task.WhenAll` over loops that never throw |
| A store outage never fails an execution | Every history/dead-letter write goes through `JobRunner.SafeStoreAsync`, which logs `StoreOperationFailed` (event 1028) and continues |
| A broken classifier never fails an execution | `JobRunner.SafeClassify` catches, logs, and falls back to `Transient` |
| Cancellation is interpreted, never swallowed | Token-source checks in the runner's catch block, before any classification |
| Checkpoints only ever move forward on success | The engine never writes checkpoints; only `context.Checkpoints.SaveAsync` does, from job code |
| `ExecutionId` is stable across retry attempts | Computed once in `RunCoreAsync` before the attempt loop (`TransientFailure_IsRetried_UntilSuccess` asserts a single distinct id) |
| Every attempt gets a clean DI scope | `_scopeFactory.CreateAsyncScope()` inside the attempt loop, disposed at the end of the attempt |
| An execution is always finalized | Status, duration, attempt count and error fields are written after the loop on every exit path |
| Overlap protection is per job | `InProcessJobLockProvider` keys its semaphores by `JobId`; a held lock skips only that job's occurrence |

## Troubleshooting

| Symptom | Likely cause | What to check |
|---|---|---|
| Job fails immediately, `FailureKind = Permanent`, `AttemptCount = 1` | A `PermanentJobException`, or an HTTP 4xx other than 408/429 | `ErrorType`/`ErrorMessage` in `WorkerKitExecutions`; is the classification actually correct for this error? |
| Job fails with `FailureKind = Misconfigured` | Corrupted checkpoint, unknown time zone, or invalid job configuration | The error message names the cause; for checkpoints, clear the row or restore a compatible payload type |
| Retries never happen for a failure you expect to be transient | The exception implements `IJobFailureHint` with `Permanent`, or a custom classifier maps it that way, or `MaxRetries = 0` | The recorded `ErrorType`; the job's `MaxRetries`; your `IJobFailureClassifier` registration |
| Everything is retried, including obvious bugs | Rule 6 — unknown exceptions default to `Transient` | Throw `PermanentJobException` for deterministic failures, or register a custom classifier |
| `AttemptCount` is 1 but the status is `Failed` and the kind is `Transient` | `MaxRetries = 0` | The job's retry configuration |
| Status `TimedOut` while individual calls are fast | The **total** timeout covers backoff delays too | `WithTimeout` vs. `MaxRetries × MaxDelay`; raise the total timeout or lower the retry budget |
| Status `Failed` with kind `Transient` on a job that hangs | An `AttemptTimeout` fired on every attempt | `JobRetryOptions.AttemptTimeout`; whether the job passes the token to its async calls |
| Executions stuck in `Running`, later `Abandoned` | The job did not observe cancellation within `ShutdownGracePeriod`, or the process was killed | Whether the job honors `cancellationToken`; `WorkerKitOptions.ShutdownGracePeriod` vs. the host's own shutdown timeout |
| Many `Cancelled` executions | Frequent restarts/deployments, or jobs that outlive the grace period | Deployment cadence; job duration vs. grace period. `Cancelled` is Information, not an error |
| Retries all fire at the same instant across jobs | `JitterFactor = 0` | Restore a non-zero `JitterFactor` (default 0.2) |
| Retry delays longer than `MaxDelay` | Jitter is applied after the cap; the ceiling is `MaxDelay × (1 + JitterFactor)` | Expected behavior — lower `JitterFactor` if the ceiling matters |
| A `Retry-After` hint makes retries *faster* than expected | The hint overrides the computed backoff unconditionally, in both directions | The source of the hint (server header, or your `TransientJobException`) |
| Occurrences skipped with `Job lock unavailable` / `previous execution still running` | The previous execution is still running and the overlap policy is `SkipNewExecution` | Execution durations vs. schedule interval; consider a longer interval or `WithFixedDelay` |
| No dead letters despite failing executions | `DeadLetterOnFailure()` was not configured, or the executions ended `Cancelled`/`TimedOut` rather than `Failed` | The job registration; the recorded status |
| Dead letters appear for failures that were never retried | Expected: the flag writes a record for every `Failed` execution, including `Permanent`/`Misconfigured` | The record's `FailureKind` and `AttemptCount` |
| The same item is dead-lettered on every run | The item's idempotency key was not marked `Completed` after quarantine | `MarkCompletedAsync` on the dead-letter path (see [idempotency.md](idempotency.md)) |
| Log: `Store operation … failed; the engine continues but durable state may be incomplete` | The execution/dead-letter store is unreachable | Database connectivity. Executions still run, but history may be missing |
| Log: `Scheduler loop for job … crashed unexpectedly` | A bug in the kit — this path should be unreachable | Report it with the logged exception; the job stops being scheduled until restart, other jobs are unaffected |

## Related

- [execution-semantics.md](execution-semantics.md) — execution states, identity, retry summary
- [checkpoints.md](checkpoints.md) — resuming after a failure
- [idempotency.md](idempotency.md) — why a retry does not duplicate side effects
- [api-integration.md](api-integration.md) — HTTP resilience, `Retry-After`, masked diagnostics

# Observability

Everything the engine emits uses BCL primitives — `ILogger` with source-generated messages,
`System.Diagnostics.Metrics.Meter`, `System.Diagnostics.ActivitySource`. There is no adapter
package to install and no ResilientWorkerKit-specific exporter: any `ILoggerProvider`,
`MeterListener` or `ActivityListener` already sees it.

## Structured logging

### Logger categories

| Category | Emitted by |
|---|---|
| `ResilientWorkerKit.Host` | startup recovery, graceful shutdown |
| `ResilientWorkerKit.Jobs.{jobId}` | one category per job — its scheduler loop and its executions |
| `ResilientWorkerKit.Http.{clientName}` | the HTTP package's safe logging handler |

Per-job categories mean a noisy job can be turned down without silencing the engine:

```jsonc
{
  "Logging": {
    "LogLevel": {
      "ResilientWorkerKit.Host": "Information",
      "ResilientWorkerKit.Jobs.reservation-sync": "Debug",
      "ResilientWorkerKit.Jobs.heartbeat": "Warning",
      "ResilientWorkerKit.Http.reservations": "Information"
    }
  }
}
```

### Log scopes

`JobRunner` opens a scope around the entire execution, so **every** entry written during it —
including entries from job code through `context.Logger` — carries these fields:

| Field | Value |
|---|---|
| `JobId` | the job's stable id |
| `ExecutionId` | stable across all retry attempts of this execution |
| `ScheduledExecutionId` | `{jobId}:{identityToken}` — the occurrence identity |
| `CorrelationId` | equal to the `ExecutionId`; the value propagated to outbound HTTP |
| `HostInstanceId` | `{machine}:{pid}` by default |

A second, nested scope adds `AttemptNumber` (1-based) around each attempt.

This is what makes a failed run diagnosable in one query: filter on `ExecutionId` and you get the
scheduler decision, every attempt, every HTTP call the job made and the final outcome — and the same
`CorrelationId` appears in the upstream API's own logs, because the HTTP pipeline sends it as
`X-Correlation-ID`.

### Log events

All engine messages are source-generated (`JobLog`) with constant templates and stable event ids.
Ids **1000–1099 are reserved for the engine** (1000–1042 in use today); job code should use its
own range.

| Id | Name | Level | Meaning |
|---|---|---|---|
| 1000 | `JobRegistered` | Information | Loop started: schedule description, run-on-startup, overlap and misfire policies |
| 1001 | `JobScheduled` | Debug | Next occurrence computed and the loop is waiting for it |
| 1002 | `MisfireDetected` | Warning | An occurrence was missed; includes how late and the policy being applied |
| 1003 | `MisfireSkipped` | Information | The missed occurrence was skipped per policy (or was already attempted) |
| 1004 | `ExecutionStarting` | Information | An execution began; carries trigger type and scheduled time |
| 1005 | `AttemptStarted` | Debug | One attempt began |
| 1006 | `ExecutionCompleted` | Information | Success, with duration and attempt count |
| 1007 | `ExecutionFailed` | Error | Terminal failure that was not retry exhaustion (permanent, misconfigured) — carries the exception |
| 1008 | `ExecutionCancelled` | Information | Cooperative cancellation observed — **explicitly not an error** |
| 1009 | `ExecutionTimedOut` | Error | The total execution timeout elapsed |
| 1010 | `RetryScheduled` | Warning | Transient failure; retry number, max retries and the computed delay — carries the exception |
| 1011 | `RetryStarted` | Information | The backoff elapsed and the next attempt is starting |
| 1012 | `RetrySucceeded` | Information | An attempt after the first succeeded |
| 1013 | `RetriesExhausted` | Error | All retries used; the execution is recorded `Failed` — carries the exception |
| 1014 | `LockAcquired` | Debug | The per-job overlap lock was taken |
| 1015 | `LockUnavailable` | Warning | The lock could not be taken within `LockAcquireTimeout`; occurrence skipped |
| 1016 | `OverlappingExecutionSkipped` | Warning | A new occurrence fired while the previous one still ran; skipped per policy |
| 1017 | `OverlappingExecutionQueued` | Information | Same, but queued behind the running execution (`QueueSingleExecution`) |
| 1018 | `CheckpointLoaded` | Debug | A checkpoint was read; type name and last update time |
| 1019 | `CheckpointSaved` | Debug | A checkpoint was written; carries the truncated summary |
| 1020 | `IdempotentItemSkipped` | Debug | A key was already completed, so the item was skipped |
| 1021 | `DeadLetterCreated` | Warning | A dead letter was written; scope, item id and reason |
| 1022 | `ShutdownStarted` | Information | Graceful shutdown began; grace period and number of running executions |
| 1023 | `ShutdownCompleted` | Information | Shutdown finished; `AllFinished` tells you whether the grace period was enough |
| 1024 | `AbandonedExecutionsRecovered` | Warning | Startup recovery marked N stale `Running` records `Abandoned` |
| 1025 | `DuplicateOccurrenceSkipped` | Information | The occurrence identity had already completed; skipped |
| 1026 | `ScheduleExhausted` | Information | The schedule yields no further occurrences; the job now waits for manual triggers only |
| 1027 | `ManualTriggerRequested` | Information | `IManualJobTrigger` requested a run |
| 1028 | `StoreOperationFailed` | Error | A durable-store call threw; the engine continues but persisted state may be incomplete — carries the exception |
| 1029 | `SchedulerLoopCrashed` | Error | Last-resort catch: a loop bug stopped scheduling this job until restart. This is a bug in ResilientWorkerKit — carries the exception |
| 1030 | `RunnerFaulted` | Error | The execution pipeline faulted instead of recording a result. This is a bug in ResilientWorkerKit — carries the exception |
| 1031 | `FollowUpQueued` | Warning | A durable follow-up retry was queued; ordinal, max attempts and due time |
| 1032 | `FollowUpStarting` | Information | A follow-up execution is starting; carries the origin occurrence |
| 1033 | `FollowUpRetriesExhausted` | Error | The follow-up chain used all attempts; the occurrence will not be retried again |
| 1034 | `FollowUpSkippedForPermanentFailure` | Information | No follow-up queued: the failure was deterministic and `RetryPermanentFailures` is off |
| 1035 | `OutOfBandWorkDeferred` | Debug | Durable work is due but the job is busy; it stays queued and is not dropped |
| 1036 | `OutOfBandWorkReturnedToQueue` | Warning | The lease was released without executing (job lock unavailable, or the run was cancelled); the occurrence is immediately acquirable again |
| 1037 | `PendingLeaseLost` | Warning | The lease could not be renewed or completed — another host may take the occurrence over; the run continues and a duplicate execution is the documented at-least-once corner |
| 1038 | `FollowUpChainResumed` | Warning | `ContinueAfterAbandoned` queued follow-up 1 for an origin execution that ended without a durable follow-up |
| 1039 | `PendingOccurrenceAlreadyQueued` | Debug | The logical occurrence is already queued; the unique index made the write a no-op |
| 1040 | `PendingLeaseNotAcquired` | Debug | Another owner holds the lease; the loop waits for its expiry |
| 1041 | `FollowUpWriteFailedRowRetained` | Warning | The next follow-up could not be written durably; the current row is kept so the occurrence re-delivers instead of the chain being lost |
| 1042 | `StalePendingOccurrenceRemoved` | Information | A pending row whose occurrence had already completed was cleaned up |

Alerting note: **1037 and 1041 are the durability warnings** — they mean the store misbehaved
at a moment the engine specifically defends, and while the engine recovers on its own, repeated
occurrences point at the database. 1038 fires at most once per resumed chain and is the audit
trail for `ContinueAfterAbandoned`.

`StoreOperationFailed` (1028) carries an `Operation` field naming the failed call:
`CreateExecution`, `UpdateExecution`, `AddDeadLetter`, `StartupRecovery`, `RecoverScheduleState`,
`CheckMissedOccurrence`, `CheckDuplicateOccurrence`, `ClassifyFailure`,
`GetNextPendingOccurrence`, `AcquirePendingLease`, `RenewPendingLease`,
`CompletePendingOccurrence`, `ReleasePendingLease`, `RemoveStalePendingOccurrence`,
`QueueFollowUpOccurrence`, `FlushUnplannedFollowUp`, `CheckFollowUpChain` or
`ResumeFollowUpChain`.

Events 1022, 1023, 1024 and the `StartupRecovery` variant of 1028 come from the
`ResilientWorkerKit.Host` category; every other event comes from the job's own
`ResilientWorkerKit.Jobs.{jobId}` category.

Job code adds one more entry through the context: `context.ReportProgress(message)` writes
`Progress: {ProgressMessage}` at Debug and updates the health snapshot.

### Level discipline

The severities are chosen so that a healthy worker is quiet and a real failure is impossible to
miss. Three rules the engine follows, and that job code should follow too:

1. **Routine operation is never logged at Error.** A skipped occurrence, a duplicate suppressed by
   identity dedup, an exhausted schedule, a queued overlap — all Information or below. If Error
   fires, something needs a human.
2. **Cancellation is Information, not Error.** `ExecutionCancelled` (1008) even says so in its
   message text. Host shutdown is a normal lifecycle event; logging it as an error trains operators
   to ignore errors, and makes every rolling deployment look like an outage. The same applies in
   the HTTP package, where `SafeHttpLoggingHandler` rethrows `OperationCanceledException` without
   logging it at all.
3. **Exceptions are always passed as objects**, never formatted into the message string. Every
   failure event (1007, 1010, 1013, 1028, 1029) takes an `Exception` parameter, so the stack trace,
   inner exceptions and exception type survive into structured sinks instead of being flattened to
   text. `logger.LogError("failed: {Error}", ex.Message)` loses everything that makes an exception
   worth logging.

A retry that eventually succeeds produces Warning (1010) then Information (1012) — visible if you
look, not a page. A retry that exhausts produces Error (1013). That gradient is intentional.

## Metrics

Meter name: **`ResilientWorkerKit`** (`WorkerKitMetrics.MeterName`).

| Instrument | Type | Unit | Tags | Description |
|---|---|---|---|---|
| `workerkit.job.executions` | `Counter<long>` | `{execution}` | `job.id`, `status` | Finished job executions by status |
| `workerkit.job.retries` | `Counter<long>` | `{retry}` | `job.id` | Retry attempts scheduled after transient failures |
| `workerkit.job.misfires` | `Counter<long>` | `{occurrence}` | `job.id`, `policy` | Missed schedule occurrences detected |
| `workerkit.job.overlap_skipped` | `Counter<long>` | `{occurrence}` | `job.id` | Occurrences skipped **or queued** because the previous execution was still running |
| `workerkit.job.dead_letters` | `Counter<long>` | `{record}` | `job.id` | Dead-letter records created |
| `workerkit.job.follow_ups` | `Counter<long>` | `{occurrence}` | `job.id` | Durable follow-up retries queued (including chains resumed by `ContinueAfterAbandoned`) |
| `workerkit.job.duration` | `Histogram<double>` | `s` | `job.id`, `status` | Job execution duration in seconds |
| `workerkit.job.running` | `UpDownCounter<long>` | `{execution}` | `job.id` | Currently running job executions |

Details worth knowing:

- `status` is the `JobExecutionStatus` name: `Completed`, `Failed`, `Cancelled`, `TimedOut`,
  `Abandoned`. `Running` never appears as a tag value — the counter and histogram are recorded once,
  at the end of an execution.
- `policy` is the `MisfirePolicy` name.
- `workerkit.job.duration` measures the whole execution including retry backoff, in **seconds**
  (the engine divides its millisecond measurement by 1000 before recording).
- `workerkit.job.running` is incremented at execution start and decremented at execution end, so it
  reads as a live gauge of concurrency per job.
- `workerkit.job.overlap_skipped` is incremented in both overlap branches. Its name says "skipped";
  its description and behavior include queued occurrences. If you need to distinguish them, use log
  events 1016 and 1017.
- `workerkit.job.dead_letters` counts both item-level dead letters written by job code through
  `context.DeadLetters` and execution-level ones written by the engine when
  `DeadLetterOnFailure()` is configured.

### The low-cardinality rule

Tags are limited to values with a small, bounded set of possibilities: a job id (one per
registration), a status (six), a misfire policy (four). That is a deliberate constraint, not an
oversight.

Every distinct tag combination is a separate time series in the backend. An `execution.id` tag would
create one series per run — thousands per day per job — and the cost is not just storage: it
degrades query performance for everything else in the same backend, and most managed metrics
services either bill for it or start dropping series. Anything with unbounded cardinality is
therefore banned from metric tags:

- execution ids and correlation ids,
- user, customer, tenant or account identifiers,
- item ids, URLs, file names, error messages, stack traces.

Those belong in **logs** (where the log scope already carries `ExecutionId` and `CorrelationId`) and
in **traces** (where per-span attributes are the norm). The rule is stated in `WorkerKitMetrics`'
own documentation: *execution ids never become tags*.

## Tracing

Activity source name: **`ResilientWorkerKit`** — the same string as the meter, exposed as
`WorkerKitMetrics.ActivitySource`.

One activity is created per execution, wrapping all of its attempts:

| | |
|---|---|
| Operation name | `workerkit.job.execute` |
| Started | when the execution record is created, before the first attempt |
| Stopped | after the final status is determined and recorded |

Tags:

| Tag | Set | Value |
|---|---|---|
| `workerkit.job.id` | at start | the job id |
| `workerkit.execution.id` | at start | the execution id |
| `workerkit.trigger` | at start | `schedule`, `startup`, `misfire`, `queued-overlap` or `manual` |
| `workerkit.attempts` | at end | total attempts performed |
| `workerkit.status` | at end | the final `JobExecutionStatus` name |

For a final status of `Failed` or `TimedOut`, the activity status is set to
`ActivityStatusCode.Error` with the (truncated, sanitized) error message as the description.

`workerkit.execution.id` is a span attribute, not a metric tag — spans are sampled and stored per
operation, so per-execution identifiers are exactly what belongs there.

Because the activity is current for the whole execution, anything downstream that participates in
`System.Diagnostics.Activity` nests under it automatically: `HttpClient` instrumentation, EF Core
instrumentation, and your own `ActivitySource` spans in job code. The HTTP package's
`CorrelationIdHandler` also falls back to `Activity.Current?.TraceId` when no explicit correlation
id was set, so the trace id and the `X-Correlation-ID` header agree.

## Wiring OpenTelemetry

No ResilientWorkerKit adapter package exists because none is needed — subscribing to the meter name
and the activity source name is the whole integration.

```csharp
using ResilientWorkerKit.Engine;   // WorkerKitMetrics.MeterName

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("reservation-worker"))
    .WithMetrics(metrics => metrics
        .AddMeter(WorkerKitMetrics.MeterName)      // "ResilientWorkerKit"
        .AddRuntimeInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithTracing(tracing => tracing
        .AddSource(WorkerKitMetrics.MeterName)     // same string, activity source
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddOtlpExporter());

// Logs: the OpenTelemetry logging provider picks up the scopes described above.
builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeScopes = true;      // required for JobId/ExecutionId/CorrelationId
    options.IncludeFormattedMessage = true;
    options.AddOtlpExporter();
});
```

`IncludeScopes = true` matters: without it the log scope fields are dropped and log entries lose the
`ExecutionId` that ties them to the trace and to the execution record.

The literal strings `"ResilientWorkerKit"` work equally well if you prefer not to reference the
engine assembly from your composition root; `WorkerKitMetrics.MeterName` is a public `const`, so
using it costs nothing at runtime.

Prometheus, Application Insights, `dotnet-counters --counters ResilientWorkerKit` and any other
`MeterListener`-based tool work the same way, for the same reason.

## Suggested dashboards and alerts

Built only from the instruments above.

**Per-job overview panel**

- Execution rate by outcome: `rate(workerkit.job.executions)` grouped by `job.id` and `status`.
- Success ratio: executions with `status="Completed"` over all executions, per `job.id`.
- Duration: p50 / p95 / p99 of `workerkit.job.duration` by `job.id`. Add the job's configured
  `WithTimeout` as a reference line — a p99 approaching it predicts `TimedOut` before it happens.
- Concurrency: current value of `workerkit.job.running` by `job.id`.
- Retry pressure: `rate(workerkit.job.retries)` by `job.id`, ideally overlaid on the failure rate.

**Alerts**

| Condition | Signal | Why |
|---|---|---|
| Any execution with `status="Failed"` or `status="TimedOut"` in the last period | `workerkit.job.executions` | The primary failure signal. Pair it with the health check for the *consecutive* view. |
| `status="Abandoned"` count > 0 | `workerkit.job.executions` | A previous host died mid-run. Always worth a look. |
| `workerkit.job.dead_letters` increasing | dead-letter counter | Items are being quarantined; the backlog needs a human. |
| Sustained `workerkit.job.overlap_skipped` | overlap counter | Occurrences are firing faster than the job can finish — the schedule interval or the job's work needs revisiting. |
| `workerkit.job.misfires` > 0 for a calendar job | misfire counter, tag `policy` | The host was down over a scheduled time. With `policy="Skip"` the occurrence was lost entirely. |
| `workerkit.job.running` stuck at ≥ 1 for longer than the expected duration | running gauge | A hung execution. The health check's stuck detection covers the same ground with per-job thresholds — see [health-checks.md](health-checks.md). |
| **No** `workerkit.job.executions` for a job over more than one schedule period | executions counter | A scheduler loop that stopped. Absence of a signal is the failure mode logs alone will not tell you about; pair with log event 1029. |
| `rate(workerkit.job.retries)` high while the failure rate stays flat | retries counter | The job is succeeding only through retries — an upstream dependency is degrading. |

**Log-based alerts** (things metrics cannot express)

- Event id **1029** (`SchedulerLoopCrashed`) at any volume — this is a kit bug and the job stops
  being scheduled until restart.
- Event id **1028** (`StoreOperationFailed`) sustained — the durable stores are failing, so
  execution history and idempotency guarantees are degrading silently.
- Event id **1023** (`ShutdownCompleted`) with `AllFinished=false` — the grace period was too short
  and executions were left `Running`, to be marked `Abandoned` at next startup.

Tie dashboards and alerts together with `JobId`: the metric tag `job.id`, the log scope field
`JobId`, the span tag `workerkit.job.id` and the health check's data-dictionary key are all the same
string.

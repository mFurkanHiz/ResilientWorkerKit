# Health Checks

`ResilientWorkerKit.HealthChecks` adapts the engine's in-memory health tracker to
`Microsoft.Extensions.Diagnostics.HealthChecks`. It is one aggregate `IHealthCheck` covering every
registered job, with a per-job entry in the result's data dictionary.

It reads state that the engine already maintains; it never touches the database and never runs a
job, so it is cheap enough to be polled every few seconds.

## Registration

```csharp
builder.Services.AddResilientWorkerKit(kit => { /* jobs */ });

builder.Services.AddHealthChecks().AddResilientWorkerKit();
```

```csharp
public static IHealthChecksBuilder AddResilientWorkerKit(
    this IHealthChecksBuilder builder,
    string name = "resilient-worker-kit",
    HealthStatus? failureStatus = null,
    IEnumerable<string>? tags = null);
```

| Parameter | Default | Meaning |
|---|---|---|
| `name` | `resilient-worker-kit` | The entry key in `HealthReport.Entries`. |
| `failureStatus` | `null` → `Unhealthy` | Status reported if the check itself throws. |
| `tags` | `null` | Standard health-check tags, used to filter endpoints. |

The check resolves `IJobHealthTracker`, `IJobRegistry` and `TimeProvider` from DI, all of which
`AddResilientWorkerKit` registers — so it must be called after (or alongside) the engine
registration. Because `TimeProvider` is injected, the whole evaluation is testable with
`FakeTimeProvider`; `WorkerKitHealthCheckTests` does exactly that.

Exposing it in a `WebApplication`:

```csharp
app.MapHealthChecks("/health");
```

Per-job thresholds are configured on the job, not on the check:

```csharp
kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
    .WithInterval(TimeSpan.FromMinutes(5))
    .WithTimeout(TimeSpan.FromMinutes(2))
    .WithHealthThresholds(t =>
    {
        t.DegradedAfterConsecutiveFailures  = 2;
        t.UnhealthyAfterConsecutiveFailures = 5;
        t.UnhealthyWhenNoSuccessFor         = TimeSpan.FromMinutes(30);
        t.StuckAfter                        = TimeSpan.FromMinutes(10);
    }));
```

## The evaluation algorithm

`WorkerKitHealthCheck` iterates `IJobRegistry.Jobs` and evaluates each job independently, in this
exact order. The first rule that matches wins.

**Step 0 — disabled jobs.** If `definition.Enabled` is false, the data dictionary gets
`["{jobId}"] = "disabled"` and the job is skipped entirely. A disabled job can never make the report
anything other than healthy.

**Step 1 — never run → Healthy.** If the tracker has no snapshot for the job, or the snapshot's
`LastStartedAtUtc` is null, the result is `Healthy` with detail `not yet run`, or
`not yet run; next occurrence {timestamp}` when the next occurrence is already known.

This rule exists because the alternative is worse. A job that has not run yet is not a job that is
failing — and the two situations that produce it are both completely normal:

- a **fresh deployment**, where every job is briefly in this state and the pod would otherwise fail
  its readiness probe and never receive traffic;
- a **long-period job**: a monthly billing job is in this state for up to a month after every
  deployment. Treating that as unhealthy would page someone every release, and after enough false
  pages nobody reads the alerts.

`WorkerKitHealthCheckTests.NeverRunJob_IsHealthy_NotUnhealthy` pins this behavior.

**Step 2 — stuck detection → Degraded.** The threshold is
`thresholds.StuckAfter ?? (definition.Timeout is { } t ? t * 2 : null)`. If a limit exists, the job
`IsRunning`, `RunningSinceUtc` is set, and `now - RunningSinceUtc > limit`, the result is `Degraded`
with detail `possibly stuck: running since {timestamp} (> {limit})`.

If neither `StuckAfter` nor `WithTimeout(...)` is configured, **stuck detection is off for that
job** — there is no default.

Note the position: stuck detection runs *before* the failure-count rules, so a job that is currently
hanging reports "possibly stuck" rather than a stale failure count. It is `Degraded` and not
`Unhealthy` on purpose: a long-running execution may be legitimately slow, and the engine's own
total timeout is the mechanism that actually terminates it.

**Step 3 — consecutive failures → Unhealthy.** If
`ConsecutiveFailures >= UnhealthyAfterConsecutiveFailures`, the result is `Unhealthy` with detail
`{n} consecutive failures (last: {LastResult})`.

**Step 4 — no recent success → Unhealthy.** If `UnhealthyWhenNoSuccessFor` is configured **and**
`ConsecutiveFailures > 0` **and** `LastSuccessAtUtc` is set **and**
`now - LastSuccessAtUtc > window`, the result is `Unhealthy` with detail
`no successful execution since {timestamp}`.

All three conditions are required. In particular, a job that has **never** succeeded has a null
`LastSuccessAtUtc` and therefore never trips this rule — it is caught by the consecutive-failure
rules instead. And the `ConsecutiveFailures > 0` requirement means a job that succeeded and then
simply stopped being scheduled does not turn red through this rule.

**Step 5 — consecutive failures → Degraded.** If
`ConsecutiveFailures >= DegradedAfterConsecutiveFailures`, the result is `Degraded` with the same
detail shape as step 3.

**Step 6 — otherwise Healthy.** The detail is a comma-joined summary built from what is known:
`running` or `last result {status}`, then `last success {timestamp}` if any, then
`next {timestamp}` if a next occurrence is known.

### What counts as a failure

`ConsecutiveFailures` is maintained by `JobHealthTracker` when an execution finishes:

| Final status | Effect |
|---|---|
| `Completed` | resets the counter to 0 and sets `LastSuccessAtUtc` |
| `Failed` | increments |
| `TimedOut` | increments |
| `Cancelled` | neither — shutdown is not a failure |
| `Abandoned` | neither |

That `Cancelled` and `Abandoned` are neutral matters operationally: a rolling deployment that
cancels three in-flight executions does not push any job toward Degraded.
`WorkerKitHealthCheckTests.SuccessResetsTheFailureStreak` covers the reset.

Note that a whole execution — including all of its retry attempts — counts as one failure. Four
retries inside one execution do not make a job Unhealthy.

## Aggregate status and the data dictionary

- The reported status is the **worst individual status** across all enabled jobs
  (`Unhealthy` < `Degraded` < `Healthy`, per the framework's enum ordering). One Unhealthy job makes
  the whole check Unhealthy.
- The **description** is `"{n} job(s) healthy"` when no job has a problem — `n` being the number of
  *enabled* jobs — otherwise the semicolon-joined list of `"{jobId}: {detail}"` for every non-healthy
  job.
- The **data dictionary** has one entry per registered job, keyed by `JobId`, whose value is either
  the literal string `"disabled"` or that job's detail string. Healthy jobs appear too, so the
  dictionary is a complete per-job status view.

A typical Unhealthy response body:

```json
{
  "status": "Unhealthy",
  "entries": {
    "resilient-worker-kit": {
      "status": "Unhealthy",
      "description": "reservation-sync: 5 consecutive failures (last: Failed)",
      "data": {
        "reservation-sync": "5 consecutive failures (last: Failed)",
        "notification-dispatch": "last result Completed, last success 2026-08-01T10:04:12Z, next 2026-08-01T10:05:12Z",
        "monthly-billing": "not yet run; next occurrence 2026-08-05T07:30:00Z",
        "legacy-import": "disabled"
      }
    }
  }
}
```

Timestamps in details are formatted as `yyyy-MM-ddTHH:mm:ssZ` in UTC, invariant culture.

## `JobHealthThresholds`

Configured per job with `WithHealthThresholds(...)`.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `DegradedAfterConsecutiveFailures` | `int` | `2` | Consecutive failures after which the job reports Degraded. |
| `UnhealthyAfterConsecutiveFailures` | `int` | `5` | Consecutive failures after which the job reports Unhealthy. |
| `UnhealthyWhenNoSuccessFor` | `TimeSpan?` | `null` (off) | Report Unhealthy when the job has run, has at least one consecutive failure, has succeeded at some point, and that last success is older than this window. |
| `StuckAfter` | `TimeSpan?` | `null` | A running execution older than this reports Degraded. When null, falls back to **2 × the job's total timeout**, and if no timeout is configured, stuck detection is disabled. |

## `JobHealthSnapshot`

The read model behind the check, also available directly through `IJobHealthTracker.Get(jobId)` and
`.GetAll()`. It is maintained in memory by the engine and is **not** durable: after a restart, every
job is back to "not yet run" until it runs again. Durable history lives in `WorkerKitExecutions`
(see [persistence.md](persistence.md)).

| Field | Type | Meaning |
|---|---|---|
| `JobId` | `string` | The job id. |
| `Enabled` | `bool` | Whether the job participates in scheduling. |
| `IsRunning` | `bool` | True while at least one execution is in flight. |
| `RunningSinceUtc` | `DateTimeOffset?` | Start of the oldest currently-running execution; null when idle. |
| `LastScheduledAtUtc` | `DateTimeOffset?` | Scheduled time of the most recent occurrence that started. |
| `LastStartedAtUtc` | `DateTimeOffset?` | Start of the most recent execution. Null ⇒ "never run". |
| `LastCompletedAtUtc` | `DateTimeOffset?` | Completion of the most recent finished execution, whatever its outcome. |
| `LastSuccessAtUtc` | `DateTimeOffset?` | Completion of the most recent `Completed` execution. |
| `LastFailureAtUtc` | `DateTimeOffset?` | Time of the most recent `Failed` or `TimedOut` execution. |
| `LastResult` | `JobExecutionStatus?` | Status of the most recent finished execution. |
| `LastDurationMs` | `double?` | Duration of the most recent finished execution. |
| `ConsecutiveFailures` | `int` | Failed/timed-out executions since the last success. |
| `NextOccurrenceUtc` | `DateTimeOffset?` | Next planned occurrence, when the schedule yields one. |
| `LastProgress` | `string?` | Most recent `context.ReportProgress(...)` note; cleared when an execution starts. |
| `LastCheckpointSummary` | `string?` | Summary of the last checkpoint saved. |

Use it directly to build a status page — the reservation sample's `/` endpoint does:

```csharp
app.MapGet("/jobs", (IJobHealthTracker tracker) => Results.Ok(
    tracker.GetAll().Select(s => new
    {
        s.JobId, s.IsRunning, s.ConsecutiveFailures,
        lastResult = s.LastResult?.ToString(),
        s.LastSuccessAtUtc, s.NextOccurrenceUtc,
        s.LastProgress, s.LastCheckpointSummary,
    })));
```

## Choosing thresholds per job type

The defaults (Degraded at 2, Unhealthy at 5, no staleness window, stuck detection from the timeout)
are tuned for a job that runs often. **They are wrong for a monthly job**, and this is the single
most common misconfiguration.

The rule of thumb: every threshold expressed in *failures* should be read as a *duration*, by
multiplying it by the schedule period.

### Frequent poller — 1-minute interval

```csharp
kit.AddJob<InboxPollJob>("inbox-poll", job => job
    .WithInterval(TimeSpan.FromMinutes(1))
    .WithTimeout(TimeSpan.FromSeconds(45))
    .WithHealthThresholds(t =>
    {
        t.DegradedAfterConsecutiveFailures  = 3;    // ~3 minutes of trouble
        t.UnhealthyAfterConsecutiveFailures = 10;   // ~10 minutes — a real outage
        t.UnhealthyWhenNoSuccessFor         = TimeSpan.FromMinutes(15);
        t.StuckAfter                        = TimeSpan.FromMinutes(3);
    }));
```

Counts can be generous because they convert to short durations, and a transient upstream blip should
not turn the pod red. `UnhealthyWhenNoSuccessFor` is the useful backstop here: it catches the case
where the job alternates failure and success just often enough to keep the consecutive counter low
while making no real progress.

### Hourly job

```csharp
kit.AddJob<HourlyRollupJob>("hourly-rollup", job => job
    .WithCron("0 * * * *")
    .WithTimeout(TimeSpan.FromMinutes(20))
    .WithHealthThresholds(t =>
    {
        t.DegradedAfterConsecutiveFailures  = 1;    // one missed hour is already notable
        t.UnhealthyAfterConsecutiveFailures = 3;    // ~3 hours
        t.UnhealthyWhenNoSuccessFor         = TimeSpan.FromHours(4);
        // StuckAfter left null → 40 minutes (2 × the 20-minute timeout)
    }));
```

### Monthly job

```csharp
kit.AddJob<MonthlyBillingJob>("monthly-billing", job => job
    .MonthlyOnDay(5, new TimeOnly(10, 30), "Europe/Istanbul")
    .WithTimeout(TimeSpan.FromMinutes(30))
    .WithMisfirePolicy(MisfirePolicy.RunImmediatelyOnce)
    .WithHealthThresholds(t =>
    {
        t.DegradedAfterConsecutiveFailures  = 1;    // one failed month is a Degraded month
        t.UnhealthyAfterConsecutiveFailures = 2;    // two failed months is an incident
        t.UnhealthyWhenNoSuccessFor         = TimeSpan.FromDays(45);
        t.StuckAfter                        = TimeSpan.FromHours(2);
    }));
```

Why the defaults break here:

- `UnhealthyAfterConsecutiveFailures = 5` means **five months** of failed billing before the check
  goes red. By then the damage is done. For any job whose period is measured in days, set the
  failure thresholds to 1 and 2.
- `UnhealthyWhenNoSuccessFor` must be **longer than one full period plus the expected runtime**. Set
  it to 20 hours for a daily job and it will report Unhealthy every single day between the run and
  the next one — but only after a failure, which makes the alert intermittent and confusing. 45 days
  for a monthly job leaves room for a month boundary plus a retry window.
- Conversely, note that the check is **silent between occurrences**: it reports the last known
  result, so a monthly job says `last result Completed, next 2026-09-05T07:30:00Z` for a month at a
  time. "The job did not run when it should have" is not something this check detects — that is a
  metrics question (an absence of `workerkit.job.executions`, see
  [observability.md](observability.md)) or a misfire alert on log event 1002.

### Stuck detection

`StuckAfter` should exceed the job's realistic worst-case runtime with margin. The 2× total timeout
fallback is well calibrated when a timeout is configured, because the timeout already encodes the
worst acceptable runtime; but it only fires if the execution outlives *twice* that, which in practice
means the total timeout failed to cancel it — a job ignoring its cancellation token. Set `StuckAfter`
explicitly when you want earlier warning, and remember that a job with **no** `WithTimeout` and no
`StuckAfter` gets no stuck detection at all.

## Kubernetes and probes

Health checks answer two different questions, and this check only answers one of them well.

**Do not wire this check to a liveness probe.** A failing job is almost never a reason to kill the
process:

- The usual cause is an external dependency — an API returning 500s, a database refusing
  connections. Restarting the pod does not fix it, and a crash-loop makes recovery slower once the
  dependency comes back.
- Restarting kills in-flight executions. They are left `Running`, get marked `Abandoned` at the next
  startup, and any work not yet checkpointed is redone.
- The failure is already isolated by design: a failing job cannot affect the host or other jobs (see
  [architecture.md](architecture.md)). Turning that isolation into a process restart throws the
  guarantee away.

A worker's liveness probe should test that the process is alive and the host is responsive, nothing
more:

```csharp
// Liveness: no checks at all — it passes if the process serves the request.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});
```

**Readiness** is only meaningful for a worker that also serves traffic. If you use it, tag the check
and decide deliberately whether job health should gate traffic:

```csharp
builder.Services.AddHealthChecks().AddResilientWorkerKit(tags: ["jobs"]);

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),   // deliberately excludes "jobs"
});

// Job health as its own endpoint, for scraping and alerting rather than orchestration.
app.MapHealthChecks("/health/jobs", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("jobs"),
});
```

Recommended split:

| Probe / endpoint | Includes the WorkerKit check | Consequence of failure |
|---|---|---|
| `/health/live` | no | pod restart |
| `/health/ready` | usually no | pod removed from load balancing |
| `/health/jobs` | yes | an alert reaches a human |

Two more practical notes:

- With the default ASP.NET Core status mapping, `Degraded` returns **HTTP 200** and `Unhealthy`
  returns **503**. Degraded is a dashboard signal, not a probe failure — which fits its meaning here
  (a failure streak that has not yet crossed the serious threshold, or a possibly-stuck execution).
- The "never run is Healthy" rule (step 1) is what lets a freshly deployed pod pass immediately. If
  you ever change that behavior for your own reasons, give the probe an `initialDelaySeconds`
  greater than the longest schedule period — which for a monthly job is not a workable number, which
  is the point of the rule.

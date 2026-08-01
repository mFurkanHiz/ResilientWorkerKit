# Scheduling

A job has **exactly one schedule** (or none, in which case it only runs on startup and via manual
triggers). Schedules are pure functions: `IJobSchedule.GetOccurrenceAfter(afterUtc, context)`
returns the earliest occurrence strictly after `afterUtc`, or `null` when the schedule is
exhausted. They never read the wall clock — the current time, the last completion time and the
job's time zone all arrive through `ScheduleCalculationContext`.

Everything time-dependent (delays, misfire detection, DST math) is driven by `TimeProvider`, so
every behavior described here is reproducible in tests with `FakeTimeProvider`.

```csharp
services.AddResilientWorkerKit(kit =>
{
    kit.AddJob<ReservationSyncJob>("reservation-sync", job =>
    {
        job.WithInterval(TimeSpan.FromMinutes(5));
        job.RunOnStartup();
        job.PreventOverlappingExecutions();          // SkipNewExecution (the default)
    });
});
```

## Schedule types at a glance

| Fluent call | Implementation | Next occurrence is | `IdentityToken` example | Default misfire policy |
|---|---|---|---|---|
| `WithInterval(TimeSpan)` | `IntervalSchedule` | previous **scheduled** time + interval | `2026-08-01T10:05:00.000Z` | `Skip` |
| `WithFixedDelay(TimeSpan)` | `FixedDelaySchedule` | previous **completion** + delay | `2026-08-01T10:09:00.000Z` | `RescheduleFromNow` |
| `WithCron(string, tz?)` | `CronSchedule` | next match of the expression in the job's zone | `2026-08-01T10:05:00Z` | `Skip` |
| `DailyAt(TimeOnly, tz?)` | `DailySchedule` | the next day at the local time | `2026-08-02T02:00` | `Skip` |
| `WeeklyAt(days, TimeOnly, tz?)` | `WeeklySchedule` | the next selected weekday at the local time | `2026-08-05T09:30` | `Skip` |
| `MonthlyOnDay(day, TimeOnly, tz?, policy)` | `MonthlySchedule` | the configured day of the next month | `2026-08` | `Skip` |
| `OnLastDayOfMonth(TimeOnly, tz?)` | `LastDayOfMonthSchedule` | the actual last day of the next month | `2026-08` | `Skip` |
| `OnceAt(DateTimeOffset)` | `OneTimeSchedule` | the single configured instant | `once:2026-09-01T12:00:00Z` | `RunImmediatelyOnce` |
| `WithSchedule(IJobSchedule)` | your type | whatever you return | whatever you return | `Skip` |
| *(no schedule call)* | — | never — startup/manual only | — | `Skip` |

Calling two schedule methods on the same job throws `JobConfigurationException`
("A job takes exactly one schedule.").

## Interval — fixed rate

`WithInterval(TimeSpan)` anchors each occurrence to the previous **scheduled** time, so the
cadence is independent of how long executions take:

```csharp
job.WithInterval(TimeSpan.FromMinutes(5));
```

- `next = previousScheduledTime + interval`. The completion time in the context is ignored.
- Non-positive intervals are rejected with `JobConfigurationException`.
- On a fresh start with no execution history the anchor is the current time, so the first
  occurrence is `hostStart + interval` — the job does not run immediately unless you also call
  `RunOnStartup()`.
- Occurrences are UTC arithmetic; DST transitions do not stretch or shrink an interval.

## Fixed delay — fixed gap between runs

`WithFixedDelay(TimeSpan)` anchors to the previous **completion**:

```csharp
job.WithFixedDelay(TimeSpan.FromMinutes(5));
```

- `next = lastCompletion + delay` when a completion later than the previous occurrence is known,
  otherwise `next = previousOccurrence + delay`.
- The effective period is `executionDuration + delay`, so successive executions can never crowd
  each other.
- Non-positive delays are rejected with `JobConfigurationException`.

### The difference, worked out

Same 5-minute setting, one execution that starts at 10:00 and takes 4 minutes (completes 10:04):

| | Anchor | Next occurrence | Gap after completion |
|---|---|---|---|
| `WithInterval(5 min)` | previous scheduled time 10:00 | **10:05** | 1 minute |
| `WithFixedDelay(5 min)` | completion 10:04 | **10:09** | 5 minutes |

(Verified in `IntervalAndFixedDelayScheduleTests.FixedDelay_DiffersFromInterval_WhenExecutionIsSlow`.)

A second example: a 10-minute fixed delay, execution scheduled at 10:00 that completes at 10:07,
fires next at **10:17**. Without any completion history the same schedule fires at 10:10.

Choose **interval** when the cadence matters (poll every 5 minutes, on the clock). Choose **fixed
delay** when the *gap* matters (always let the downstream system breathe for 5 minutes, no matter
how long the batch took).

## Cron

```csharp
job.WithCron("0 2 * * *", "Europe/Istanbul");     // 02:00 local, every day
job.WithCron("*/5 * * * *");                      // every 5 minutes (UTC — no zone configured)
job.WithCron("30 * * * * *");                     // 6 fields: at second 30 of every minute
```

Parsing and occurrence calculation are delegated to [Cronos](https://github.com/HangfireIO/Cronos)
0.8.4. The number of space-separated tokens decides the format:

| Tokens | `CronFormat` | Field order |
|---|---|---|
| 6 | `IncludeSeconds` | `second minute hour day-of-month month day-of-week` |
| anything else (normally 5) | `Standard` | `minute hour day-of-month month day-of-week` |

Supported field values and characters (Cronos grammar):

| Field | Values | Special characters |
|---|---|---|
| second (optional) | 0–59 | `*` `,` `-` `/` |
| minute | 0–59 | `*` `,` `-` `/` |
| hour | 0–23 | `*` `,` `-` `/` |
| day of month | 1–31 | `*` `,` `-` `/` `L` `W` `?` |
| month | 1–12 or `JAN`–`DEC` | `*` `,` `-` `/` |
| day of week | 0–6 or `SUN`–`SAT` (0 and 7 = Sunday) | `*` `,` `-` `/` `#` `L` `?` |

Also supported: reversed ranges (`22-1`), the macros `@every_second`, `@every_minute`, `@hourly`,
`@daily`, `@midnight`, `@weekly`, `@monthly`, `@yearly`, `@annually`, and combinations such as
`LW` (last weekday of month) or `6#3` (third Saturday). Because the format is chosen by counting
tokens, a macro (one token) is always parsed with `CronFormat.Standard`.

Two behaviors that surprise people coming from other schedulers:

- When **both** day-of-month and day-of-week are restricted, Cronos ANDs them (`0 0 13 * 5` means
  *Friday the 13th*), whereas Vixie cron ORs them.
- Full names (`SEPTEMBER`, `MONDAY`) are not supported — only the three-letter abbreviations.

Cron expressions are evaluated **in the job's time zone**: `CronExpression.GetNextOccurrence` is
called with the job's `TimeZoneInfo`, so `0 2 * * *` in `Europe/Istanbul` produces 23:00 UTC the
previous day. DST handling for cron is Cronos's (Unix cron semantics): occurrences are not skipped
when the clock jumps forward, interval-based occurrences are not skipped when it jumps backward,
and non-interval occurrences are not repeated when it jumps backward. Cron does **not** go through
`LocalTimeConverter` — that converter serves the calendar schedules below.

An invalid expression throws `JobConfigurationException` from the `CronSchedule` constructor, i.e.
during registration, before the host starts.

## Daily

```csharp
job.DailyAt(new TimeOnly(2, 0), "Europe/Istanbul");
```

Fires every day at a fixed local wall-clock time. Worked example: 02:00 in `Europe/Istanbul`
(UTC+3, no DST) evaluated after `2026-08-01T10:00Z` yields

| Property | Value |
|---|---|
| `ScheduledAtUtc` | `2026-08-01T23:00Z` |
| `ScheduledLocalTime` | `2026-08-02 02:00` |
| `IdentityToken` | `2026-08-02T02:00` |

## Weekly

```csharp
job.WeeklyAt([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
             new TimeOnly(9, 30), "Europe/Istanbul");
```

Fires on the selected weekdays at a fixed local time. From Tuesday `2026-08-04T12:00Z` the next
occurrence is Wednesday `2026-08-05T06:30Z` (09:30 Istanbul). An empty day set is rejected with
`JobConfigurationException`.

## Monthly and last-day-of-month

```csharp
job.MonthlyOnDay(5, new TimeOnly(10, 30), "Europe/Istanbul", MonthlyInvalidDayPolicy.SkipMonth);
job.OnLastDayOfMonth(new TimeOnly(23, 0), "Europe/Istanbul");
```

Both produce a `yyyy-MM` identity token, i.e. at most one completed execution per calendar month.
See [monthly-scheduling.md](monthly-scheduling.md) for the invalid-day policies, leap years,
restart behavior and misfire details.

## One-time

```csharp
job.OnceAt(DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
```

Fires exactly once and then returns `null` forever; the loop logs *"Schedule produces no further
occurrences; job now waits for manual triggers only"* once and idles.

A one-time schedule whose instant is already in the past when the host starts is treated as a
misfire — and its default misfire policy is `RunImmediatelyOnce`, so it runs once, immediately. Its
identity (`once:<instant>`) makes that safe across restarts: once an execution for that identity
has completed, it is never started again.

## Explicit times — planned one-off actions

For actions whose times are known in advance and are not a repeating pattern: a sale opening, a
campaign start, a migration cut-over.

```csharp
// Exact instants
job.AtTimes(
    DateTimeOffset.Parse("2026-08-15T07:00:00Z"),
    DateTimeOffset.Parse("2026-08-15T11:00:00Z"),
    DateTimeOffset.Parse("2026-08-16T07:00:00Z"));

// Wall-clock times in a zone (10:00 Istanbul on two consecutive days)
job.AtLocalTimes("Europe/Istanbul",
    new DateTime(2026, 8, 15, 10, 0, 0),
    new DateTime(2026, 8, 16, 10, 0, 0));

// Three runs on the 15th, four hours apart
job.Repeating(DateTimeOffset.Parse("2026-08-15T07:00:00Z"), every: TimeSpan.FromHours(4), count: 3);
```

Instants are sorted and de-duplicated; the order you pass them in does not matter. Each one is a
separate occurrence with identity `at:<instant>`, so a completed instant is never repeated after a
restart, and the schedule returns `null` once the last one has been handled.

`AtLocalTimes` resolves through the same daylight-saving rules as every other calendar schedule
(see [Local → UTC conversion and DST](#local--utc-conversion-and-dst)).

Like one-time, the default misfire policy is `RunImmediatelyOnce`: a planned action is scheduled
because it must happen, so being down at that minute should not silently drop it.

### Looking backwards on the first start

A schedule declares whether a host starting with **no execution history** should look for
occurrences already in the past:

| Schedule | Looks back on first start | Why |
|---|---|---|
| `AtTimes` / `AtLocalTimes` / `Repeating` / `OnceAt` | **Yes** | The occurrence exists precisely so it happens; the misfire policy then decides |
| Interval, fixed delay, cron, daily, weekly, monthly, last-day | No | A fresh deployment of an hourly job must not try to replay every past hour |

Custom schedules opt in by overriding `DiscoverPastOccurrencesOnFirstStart`, a default interface
member that returns `false`.

> Once a job has execution history, the anchor comes from that history and this flag no longer
> applies — it only governs the very first start against an empty store.

## Custom schedules

`WithSchedule(IJobSchedule)` accepts any implementation. Return occurrences strictly after
`afterUtc`, produce a deterministic `IdentityToken` (the engine's duplicate suppression is only as
good as that token), and keep the method free of wall-clock access. Custom schedules are not
treated as calendar schedules by validation, so `RescheduleFromNow` is allowed for them. Override
`DiscoverPastOccurrencesOnFirstStart` if your schedule represents planned instants rather than a
recurring pattern.

## Run on startup

`RunOnStartup()` is orthogonal to the schedule — it combines with any of the types above, and with
no schedule at all.

```csharp
job.WithInterval(TimeSpan.FromMinutes(30)).RunOnStartup();
```

- One execution starts immediately when the loop starts, before the first schedule delay, with
  `ScheduledAtUtc = now` and trigger type `startup`.
- Its identity is `startup:<UTC instant with milliseconds>`, e.g.
  `startup:2026-08-01T10:00:00.000Z`. This token is unique per host start, so a startup run is
  never suppressed by the completed-occurrence check — every restart really does run the job.
- **It does not shift schedule phase.** Anchor recovery only considers execution records whose
  trigger type is `schedule`, `misfire` or `queued-overlap`; `startup` and `manual` records are
  ignored. A job that runs on every deployment still fires its regular occurrences on the
  original grid.
- The startup run participates in overlap protection like any other execution: with a policy other
  than `AllowConcurrentExecutions`, a schedule occurrence arriving while the startup run is still
  going is skipped or queued.

## Time zones

`WithTimeZone("Europe/Istanbul")`, or the `timeZone` parameter of `DailyAt` / `WeeklyAt` /
`MonthlyOnDay` / `OnLastDayOfMonth` / `WithCron` (which simply calls `WithTimeZone`). The default
is **UTC**.

- Ids are resolved with `TimeZoneInfo.FindSystemTimeZoneById`. Use **IANA** ids
  (`Europe/Istanbul`, `America/New_York`, `UTC`); .NET maps them to Windows ids automatically.
- An unknown id fails registration with `JobConfigurationException` — it never silently falls back
  to UTC.
- Interval and fixed-delay schedules are pure UTC arithmetic; the time zone only affects the
  `ScheduledLocalTime` reported to the job.

### Local → UTC conversion and DST

Calendar schedules (daily, weekly, monthly, last-day-of-month) build a local wall-clock
`DateTime` and convert it through `LocalTimeConverter`, which applies two explicit rules:

| Situation | Rule | Example (`Europe/Berlin`) |
|---|---|---|
| **Spring forward** — the local time does not exist | Shift forward in 15-minute steps until the gap ends (up to 3 hours) | Daily 02:30 on 2026-03-29 (02:00→03:00 gap) runs at **03:00 local = 01:00 UTC** — the day is not silently skipped |
| **Fall back** — the local time exists twice | Take the **first** occurrence (the larger UTC offset, i.e. the earlier instant) | Daily 02:30 on 2026-10-25 runs at **00:30 UTC** (+02:00); the second 02:30 (01:30 UTC) is never produced — the next occurrence is 2026-10-26 02:30 local = 01:30 UTC |

Both rules are verified in `CalendarScheduleTests`. Note that the fall-back rule is enforced twice
over: the schedule itself does not emit the repeated wall-clock time, and even if it did, its
`IdentityToken` would be identical to the one already executed — and **a completed occurrence
identity is never run again** (see below).

## Occurrence identity

Every occurrence carries an `IdentityToken`. The engine combines it with the job id to form the
`ScheduledExecutionId` that is stored on the execution record:

```text
ScheduledExecutionId = "<jobId>:<IdentityToken>"      e.g. "monthly-billing:2026-08"
```

| Source | Token format | Example |
|---|---|---|
| `IntervalSchedule` | scheduled UTC, `yyyy-MM-ddTHH:mm:ss.fffZ` | `2026-08-01T10:05:00.000Z` |
| `FixedDelaySchedule` | scheduled UTC, `yyyy-MM-ddTHH:mm:ss.fffZ` | `2026-08-01T10:09:00.000Z` |
| `CronSchedule` | scheduled UTC, `yyyy-MM-ddTHH:mm:ssZ` | `2026-08-01T10:05:00Z` |
| `DailySchedule` | scheduled **local** time, `yyyy-MM-ddTHH:mm` | `2026-08-02T02:00` |
| `WeeklySchedule` | scheduled **local** time, `yyyy-MM-ddTHH:mm` | `2026-08-05T09:30` |
| `MonthlySchedule` | local `yyyy-MM` | `2026-08` |
| `LastDayOfMonthSchedule` | local `yyyy-MM` | `2026-08` |
| `OneTimeSchedule` | `once:` + UTC instant | `once:2026-09-01T12:00:00Z` |
| Run-on-startup | `startup:` + UTC instant with ms | `startup:2026-08-01T10:00:00.000Z` |
| Manual trigger | `manual:` + execution id | `manual:01J8…` |

Before starting a scheduled occurrence, the loop asks the execution store whether an execution for
that identity has already **completed**. If so, the occurrence is skipped and logged
("Occurrence `<id>` already completed; skipping duplicate"). This single mechanism covers the
monthly once-per-month guarantee, one-time jobs, and the DST fall-back hour. If the store call
itself fails, the engine assumes *not* completed and runs the occurrence — at-least-once is the
documented contract (see [execution-semantics.md](execution-semantics.md)).

## Misfire policies

A **misfire** is an occurrence whose scheduled time already lies in the past when the loop computes
it — in practice, after the host was down, after a long maintenance window, or when a job is
enabled again with old history. It is detected by comparing the occurrence computed from the
recovered anchor against `TimeProvider.GetUtcNow()`, logged at `Warning`
(*"Misfire detected: occurrence … was missed (late by …)"*) and counted by the
`workerkit.job.misfires` metric.

Only the **most recently missed** occurrence is ever considered. The loop walks the schedule
forward to find it; older missed occurrences are never backfilled.

| Policy | Behavior |
|---|---|
| `Skip` | Do not run the missed occurrence. The anchor advances to it and the next regular occurrence is used. |
| `RunImmediatelyOnce` | Run the most recently missed occurrence once, immediately, **keeping its original `ScheduledAtUtc` and identity**, with trigger type `misfire`. Then return to the regular grid. |
| `RunIfWithinTolerance` | Same as `RunImmediatelyOnce`, but only if `now - scheduledAt <= tolerance`; otherwise behaves like `Skip`. |
| `RescheduleFromNow` | Discard the missed occurrence, re-anchor the schedule to the current time and continue from there. |

### Support and defaults per schedule type

| Schedule | `Skip` | `RunImmediatelyOnce` | `RunIfWithinTolerance` | `RescheduleFromNow` | **Default** |
|---|:--:|:--:|:--:|:--:|---|
| `IntervalSchedule` | yes | yes | yes | yes | `Skip` |
| `FixedDelaySchedule` | yes | yes | yes | yes | **`RescheduleFromNow`** |
| `CronSchedule` | yes | yes | yes | **rejected** | `Skip` |
| `DailySchedule` | yes | yes | yes | **rejected** | `Skip` |
| `WeeklySchedule` | yes | yes | yes | **rejected** | `Skip` |
| `MonthlySchedule` | yes | yes | yes | **rejected** | `Skip` |
| `LastDayOfMonthSchedule` | yes | yes | yes | **rejected** | `Skip` |
| `OneTimeSchedule` | yes | yes | yes | **rejected** | **`RunImmediatelyOnce`** |
| custom `IJobSchedule` | yes | yes | yes | yes | `Skip` |

The defaults come from the schedule type, not from a global setting: a fixed-delay job is defined
relative to "now" anyway, so re-anchoring is the honest choice; a one-time job that was supposed to
run while the host was down still needs to run.

### Validation rules

Checked at registration time, before the host starts:

- `RunIfWithinTolerance` **requires** a tolerance:
  `WithMisfirePolicy(MisfirePolicy.RunIfWithinTolerance, TimeSpan.FromMinutes(15))`. Without it,
  `JobConfigurationException`.
- Any tolerance that is supplied must be positive.
- `RescheduleFromNow` is **rejected for calendar schedules** (cron, daily, weekly, monthly,
  last-day-of-month, one-time): "the 5th of the month" cannot be re-anchored to an arbitrary
  instant without becoming a different schedule.

### Restart safety

Misfire recovery is idempotent across restarts. Before recreating a missed occurrence, the engine
checks whether **any** execution record exists for its `ScheduledExecutionId` — regardless of
status. A missed occurrence that was already attempted and failed before a crash is therefore not
attempted a second time by the recovery path; the loop skips it and continues with the next regular
occurrence. If that store check fails, the engine assumes the occurrence *was* attempted (it
prefers skipping over double-running).

Note the asymmetry with the duplicate check described earlier:

| Check | When | Counts as "already handled" |
|---|---|---|
| Duplicate suppression | before firing any occurrence | only `Completed` records |
| Misfire recovery | before recreating a missed occurrence | **any** record, any status |

### Worked example

Interval of 5 minutes, the last handled occurrence recorded at `09:48Z`, host starts at `10:00Z`.
Occurrences at `09:53` and `09:58` were missed.

| Policy | What happens |
|---|---|
| `Skip` (default) | Nothing runs now; the anchor moves to `09:58` and the next execution is at **`10:03`**. |
| `RunImmediatelyOnce` | One execution starts now with `ScheduledAtUtc = 09:58` and trigger `misfire`; `09:53` is never run; the grid continues at `10:03`. |
| `RunIfWithinTolerance`, tolerance 5 min | Lateness is 2 minutes ≤ 5 → same as `RunImmediatelyOnce`. |
| `RunIfWithinTolerance`, tolerance 1 min | Lateness is 2 minutes > 1 → same as `Skip`. |
| `RescheduleFromNow` | The grid is re-anchored to `10:00`; the next execution is at **`10:05`**. |

> Misfire detection depends on execution history being durable. With the default **in-memory**
> execution store, history is lost when the process exits, so after a restart every schedule
> re-anchors to "now" and no misfire is ever detected. Configure the EF Core store if you want
> misfire policies to have any effect across restarts.

## Overlap policies

`PreventOverlappingExecutions(OverlapPolicy)` / `AllowConcurrentExecutions()` decide what happens
when an occurrence becomes due while the previous execution of the **same job** is still running.

| Policy | Behavior |
|---|---|
| `SkipNewExecution` *(default)* | The occurrence is dropped and logged at `Warning`. The next regular occurrence runs normally. |
| `QueueSingleExecution` | At most **one** occurrence is remembered; it starts as soon as the running execution finishes, with trigger type `queued-overlap` and its original scheduled time and identity. Further occurrences arriving while one is queued are dropped. |
| `AllowConcurrentExecutions` | Executions run concurrently; no queue, no per-job lock. |

`SkipNewExecution` is the default because it is the only option that cannot grow unbounded work: a
job that is permanently slower than its schedule keeps exactly one execution in flight instead of
accumulating a backlog that the host can never drain. `QueueSingleExecution` bounds the backlog at
one, which is the right trade-off when *every* occurrence has to run eventually but never twice at
once.

Worked example (5-minute interval, first execution held for 10+ minutes):

- `SkipNewExecution`: the two occurrences that fall inside the long run are both skipped; the
  second execution is the first occurrence after it completes.
- `QueueSingleExecution`: the first of the two is queued and starts immediately when the run
  finishes (trigger `queued-overlap`); the second is skipped.

Additional details:

- For any policy other than `AllowConcurrentExecutions`, `JobRunner` also takes a per-job lock
  (`IJobLockProvider`, in-process by default) as a second layer. In a multi-instance deployment
  this in-process lock does not coordinate across hosts — see the limitations doc.
- A **manual** trigger arriving while a run is in progress is rejected with an
  `InvalidOperationException` for `SkipNewExecution` and `QueueSingleExecution` (manual triggers are
  never queued); it is always accepted under `AllowConcurrentExecutions`.
- Every occurrence that hits a busy job increments the `workerkit.job.overlap_skipped` metric —
  including the one that is *queued* under `QueueSingleExecution`, so for that policy the counter
  reads "occurrences that could not start immediately" rather than "occurrences that were dropped".
  The logs distinguish the two cases (queued at `Information`, skipped at `Warning`).

## Choosing a schedule

| You want | Use | Notes |
|---|---|---|
| Poll something every N minutes, on the clock | `WithInterval` | Combine with `RunOnStartup()` if the first poll must not wait. |
| Guarantee a cool-down between runs | `WithFixedDelay` | Effective period = execution time + delay. |
| A specific local time of day | `DailyAt` | Explicit DST rules; prefer over `WithCron("0 2 * * *")` when that is all you need. |
| Specific weekdays | `WeeklyAt` | |
| A calendar rule cron expresses better (`LW`, `6#3`, ranges) | `WithCron` | Remember day-of-month AND day-of-week are ANDed. |
| Once a month on a fixed day | `MonthlyOnDay` | Decide explicitly what short months do — see [monthly-scheduling.md](monthly-scheduling.md). |
| Month-end processing | `OnLastDayOfMonth` | Handles 28/29/30/31 automatically. |
| A single planned run (migration, cutover) | `OnceAt` | Runs even if the host was down at that instant; never runs twice. |
| Only on demand | no schedule call | Use `IManualJobTrigger`; add `RunOnStartup()` if it should also run at boot. |

For the execution guarantees that surround all of this — at-least-once, checkpoints, idempotency,
retries, cancellation — see [execution-semantics.md](execution-semantics.md).

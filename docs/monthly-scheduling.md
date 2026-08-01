# Monthly Scheduling

Monthly jobs are the ones people get wrong: day 31 does not exist in April, February changes
length every four years, "the 5th at 10:30" is a different UTC instant in winter and summer, and a
host restart must not produce a second invoice run. This document describes exactly what
ResilientWorkerKit does in each of those cases.

Two schedule types are monthly:

| Fluent call | Implementation | Fires |
|---|---|---|
| `MonthlyOnDay(day, time, timeZone?, invalidDayPolicy?)` | `MonthlySchedule` | on a fixed day of the month at a fixed local time |
| `OnLastDayOfMonth(time, timeZone?)` | `LastDayOfMonthSchedule` | on the actual last day of the month at a fixed local time |

Both produce the occurrence identity `yyyy-MM` (see [Occurrence identity](#occurrence-identity-jobidyyyy-mm)).
General scheduling concepts — misfire and overlap policies, time zones, run-on-startup — are
covered in [scheduling.md](scheduling.md).

## `MonthlyOnDay`

```csharp
kit.AddJob<MonthlyBillingJob>("monthly-billing", job =>
{
    job.MonthlyOnDay(5, new TimeOnly(10, 30), "Europe/Istanbul",
                     MonthlyInvalidDayPolicy.SkipMonth);
    job.WithMisfirePolicy(MisfirePolicy.RunImmediatelyOnce);
});
```

| Parameter | Meaning | Validation |
|---|---|---|
| `dayOfMonth` | 1–31 | Outside 1–31 → `JobConfigurationException` at registration |
| `time` | Local wall-clock time (`TimeOnly`; hour, minute and second are used) | — |
| `timeZone` | IANA id; shorthand for `WithTimeZone(...)`. Default **UTC** | Unknown id → `JobConfigurationException` |
| `invalidDayPolicy` | What months without that day do. Default `SkipMonth` | `FailConfiguration` + day > 28 → `JobConfigurationException` |

The next occurrence is computed by starting at the **local** calendar month of the reference
instant and walking forward month by month until a valid day produces an instant strictly after it.
Because the comparison is strict, the month that just fired can never be produced again.

Worked example (verified in `MonthlyScheduleTests`): `MonthlyOnDay(5, 10:30, "Europe/Istanbul")`
evaluated after `2026-08-01T00:00Z`:

| Property | Value |
|---|---|
| `ScheduledAtUtc` | `2026-08-05T07:30Z` |
| `ScheduledLocalTime` | `2026-08-05 10:30` |
| `IdentityToken` | `2026-08` |
| next occurrence after it | `2026-09-05T07:30Z`, identity `2026-09` |

## Invalid-day policies

`MonthlyInvalidDayPolicy` decides what happens in months that do not have the configured day.

| Policy | Behavior |
|---|---|
| `SkipMonth` *(default)* | No occurrence at all in that month. |
| `RunOnLastAvailableDay` | The occurrence moves to the last day of that month (28, 29 or 30). |
| `FailConfiguration` | A day that cannot exist in every month (29, 30, 31) is rejected at registration with `JobConfigurationException`, forcing an explicit decision. Days 1–28 are accepted and behave identically under all three policies. |

Which days are ever affected:

| Configured day | Months without it |
|---|---|
| 1–28 | none — the policy is irrelevant |
| 29 | February of non-leap years |
| 30 | February |
| 31 | February, April, June, September, November |

### Day 31 across a full year (2026, non-leap)

`MonthlyOnDay(31, new TimeOnly(12, 0), invalidDayPolicy: …)`:

| Month | Days in month | `SkipMonth` | `RunOnLastAvailableDay` |
|---|---:|---|---|
| January 2026 | 31 | 2026-01-31 12:00 | 2026-01-31 12:00 |
| February 2026 | 28 | *(no run)* | 2026-02-28 12:00 |
| March 2026 | 31 | 2026-03-31 12:00 | 2026-03-31 12:00 |
| April 2026 | 30 | *(no run)* | 2026-04-30 12:00 |
| May 2026 | 31 | 2026-05-31 12:00 | 2026-05-31 12:00 |
| June 2026 | 30 | *(no run)* | 2026-06-30 12:00 |
| July 2026 | 31 | 2026-07-31 12:00 | 2026-07-31 12:00 |
| August 2026 | 31 | 2026-08-31 12:00 | 2026-08-31 12:00 |
| September 2026 | 30 | *(no run)* | 2026-09-30 12:00 |
| October 2026 | 31 | 2026-10-31 12:00 | 2026-10-31 12:00 |
| November 2026 | 30 | *(no run)* | 2026-11-30 12:00 |
| December 2026 | 31 | 2026-12-31 12:00 | 2026-12-31 12:00 |
| **Runs per year** | | **7** | **12** |

`FailConfiguration` has no column: with day 31 the host does not start at all — registration throws
`JobConfigurationException` telling you to pick a day ≤ 28 or to choose `SkipMonth` /
`RunOnLastAvailableDay` explicitly.

Two of these rows are covered directly by tests: with `SkipMonth`, the occurrence following
2026-01-31 is **2026-03-31** (February skipped) and the one after that is **2026-05-31** (April
skipped); with `RunOnLastAvailableDay`, the occurrence following 2026-01-31 is **2026-02-28**.

With `SkipMonth` the largest possible gap is two months (January → March), so the schedule never
"loses" more than one month in a row.

### Leap years

`RunOnLastAvailableDay` uses `DateTime.DaysInMonth`, so leap years are handled without a special
case:

| Configured day | February 2026 (28 d) | February 2028 (29 d) |
|---|---|---|
| 28 | Feb 28 | Feb 28 |
| 29, `SkipMonth` | *(no run)* | Feb 29 |
| 29, `RunOnLastAvailableDay` | Feb 28 | Feb 29 |
| 31, `SkipMonth` | *(no run)* | *(no run)* |
| 31, `RunOnLastAvailableDay` | Feb 28 | **Feb 29** (verified in `MonthlyScheduleTests`) |

Note the second row: with `SkipMonth`, day 29 runs in leap Februaries and is skipped otherwise — a
genuinely irregular schedule. If you mean "end of February", use `OnLastDayOfMonth`.

## `OnLastDayOfMonth`

```csharp
job.OnLastDayOfMonth(new TimeOnly(23, 0), "Europe/Istanbul");
```

Fires on the actual last day of every month — no policy needed, no month ever skipped. Verified
across month lengths:

| Reference instant | Next occurrence (23:00 local) |
|---|---|
| 2026-02-01 | **2026-02-28** (28-day February) |
| 2028-02-01 | **2028-02-29** (leap February) |
| 2026-04-10 | **2026-04-30** (30-day month) |
| 2026-08-01 | **2026-08-31** (31-day month) |
| 2026-12-31 23:00 (that occurrence just fired) | **2027-01-31** (year rollover) |

With `Europe/Istanbul`, the 2026-08-31 occurrence at 23:00 local is `2026-08-31T20:00Z`, identity
`2026-08`.

`OnLastDayOfMonth(time)` is equivalent to `MonthlyOnDay(31, time, …, RunOnLastAvailableDay)` in
terms of the days chosen, but it says what it means and cannot be misconfigured.

## Time zones

The configured time is a **local wall-clock time** in the job's zone (default UTC). The engine
converts it to UTC when computing the occurrence, and hands the job both values
(`context.ScheduledAtUtc`, `context.ScheduledLocalTime`, `context.TimeZoneId`).

Day 5 at 10:30 local:

| Time zone | Offset at that moment | `ScheduledAtUtc` |
|---|---|---|
| `UTC` | +00:00 | 10:30Z |
| `Europe/Istanbul` (no DST) | +03:00 | **07:30Z** (verified in tests) |
| `Europe/Berlin` in winter (CET) | +01:00 | 09:30Z |
| `Europe/Berlin` in summer (CEST) | +02:00 | 08:30Z |
| `America/New_York` in winter (EST) | −05:00 | 15:30Z |
| `America/New_York` in summer (EDT) | −04:00 | 14:30Z |

Consequences worth internalizing:

- **The UTC instant of a monthly job moves when DST starts and ends.** The *local* time stays
  fixed; that is the point of configuring a zone.
- **The UTC date can differ from the local date.** `MonthlyOnDay(1, 00:30, "Europe/Istanbul")`
  fires at `21:30Z on the last day of the previous UTC month` — but its identity is still the
  **local** month (`2026-09` for the local 2026-09-01 occurrence), because the identity is built
  from the local year/month, not from the UTC instant.
- If the configured local time falls into a DST **spring-forward gap** on that day, the occurrence
  shifts forward to the end of the gap; if it falls into an **ambiguous fall-back hour**, the first
  (earlier) instant is used and the hour never fires twice. The rules and examples are in
  [scheduling.md](scheduling.md#local--utc-conversion-and-dst).

## Occurrence identity `jobId:yyyy-MM`

Every monthly occurrence carries `IdentityToken = "<local year>-<local month>"`. The engine
prefixes it with the job id to form the `ScheduledExecutionId` stored on every execution record:

```text
monthly-billing:2026-08
```

Two different store checks use that identity, and the difference between them matters.

### 1. Before firing an occurrence — completed-only

```text
ExistsForScheduledExecutionAsync(jobId, "monthly-billing:2026-08", completedOnly: true)
```

If an execution for that identity has already **completed**, the occurrence is not started; the
loop logs *"Occurrence monthly-billing:2026-08 already completed; skipping duplicate"* and moves
on. Records in any other state (`Failed`, `Cancelled`, `TimedOut`, `Abandoned`, `Running`) do not
suppress it. If the store call throws, the engine assumes *not completed* and runs — at-least-once
is the documented bias.

### 2. Before recreating a **missed** occurrence — any record

```text
ExistsForScheduledExecutionAsync(jobId, "monthly-billing:2026-08", completedOnly: false)
```

Misfire recovery is stricter: **any** record for that identity, regardless of status, means the
occurrence was already attempted, so recovery skips it, advances the anchor past it and continues
with the next regular occurrence. If this store call throws, the engine assumes it *was* attempted
(preferring a skipped run over a double run).

| | Check | Purpose |
|---|---|---|
| Regular firing | completed only | Never run the same month twice **successfully** |
| Misfire recovery | any record | Never *recreate* a missed occurrence that was already attempted |

### How this survives a host restart

Three mechanisms combine:

1. **Anchor recovery.** On startup the loop reads the recent execution history and anchors the
   schedule on the newest record whose trigger type is `schedule`, `misfire` or `queued-overlap`.
   If the August occurrence already ran, the anchor is `2026-08-05T07:30Z`, so the next computed
   occurrence is September — no matter how often the host restarts in August.
2. **Completed-identity suppression.** If an occurrence with identity `2026-08` is nevertheless
   computed again (see the configuration-change example below), it is not started because a
   completed record exists.
3. **Misfire idempotency.** Recovery of a missed occurrence checks for any record first, so a
   crash-restart loop cannot produce a stream of duplicate August attempts.

A concrete case where mechanism 2 is what saves you: the job ran on 2026-08-05 and completed. You
then redeploy with `MonthlyOnDay(20, …)`. The anchor is still `2026-08-05T07:30Z`, so the next
computed occurrence is **2026-08-20** — a new instant, but identity `2026-08` again. The completed
record suppresses it, and billing does not run twice in August. The new day takes effect from
September on.

> Anchor recovery reads the 20 most recent execution records of the job and needs a durable
> execution store. With the default **in-memory** store, all history is lost when the process
> exits: after a restart the schedule re-anchors to "now", no misfire is detected, and the
> completed-identity check has nothing to find. Monthly jobs that must survive restarts require the
> EF Core store.

## Retrying a failed execution vs. a new monthly occurrence

These are different things and the engine treats them differently.

| | Retry | New occurrence |
|---|---|---|
| Trigger | transient failure inside one execution | the schedule (or misfire recovery) |
| `ExecutionId` | **unchanged** | new |
| `ScheduledExecutionId` | **unchanged** (`monthly-billing:2026-08`) | new (`monthly-billing:2026-09`) |
| `AttemptNumber` | incremented | back to 1 |
| Controlled by | `WithRetry(...)` / `WithRetryCount(...)` | the schedule + misfire policy |

Retries happen **within** a single execution, all under the same monthly identity. When retries are
exhausted, the execution is recorded `Failed` (plus an execution-level dead letter if
`DeadLetterOnFailure()` is configured) and the loop continues.

What does **not** happen: the failed August occurrence is not re-run in August. The anchor has
already moved past it, and even after a restart misfire recovery sees the existing `Failed` record
for `monthly-billing:2026-08` and refuses to recreate it. The same is true for an execution left
`Abandoned` by a process that died mid-run. The next automatic attempt is the September occurrence.

To re-run a failed month, use a manual trigger (`IManualJobTrigger`). A manual run:

- gets a **new** `ExecutionId` and the identity `manual:<executionId>` — not `2026-08`;
- is recorded with trigger type `manual` and is **excluded from anchor recovery**, so it does not
  shift the schedule;
- consequently does **not** mark `monthly-billing:2026-08` as completed. If a later configuration
  change causes the August occurrence to be computed again, the duplicate check will not suppress
  it, because no *completed* record for that identity exists.

Because the contract is at-least-once, monthly job bodies should still be idempotent per item
(`context.Idempotency`) and checkpoint their progress (`context.Checkpoints`) — see
[execution-semantics.md](execution-semantics.md).

## Misfire behavior for monthly jobs

Scenario: `MonthlyOnDay(5, 10:30, "Europe/Istanbul")`, last completed occurrence
`2026-08-05T07:30Z`. The host is down from 2026-08-06 and comes back on **2026-10-10 at 09:00Z**.
Both the September and the October occurrences were missed; the most recent missed one is
`2026-10-05T07:30Z`, late by 5 days 1 h 30 min.

| Misfire policy | Result |
|---|---|
| `Skip` *(default for monthly)* | Nothing runs now. The anchor advances to `2026-10-05T07:30Z`; the next execution is **2026-11-05T07:30Z**. Both missed months are lost. |
| `RunImmediatelyOnce` | One execution starts immediately, carrying the **original** occurrence data: `ScheduledAtUtc = 2026-10-05T07:30Z`, `ScheduledLocalTime = 2026-10-05 10:30`, identity `2026-10`, trigger type `misfire`. September is **not** backfilled. The grid then continues with 2026-11-05. |
| `RunIfWithinTolerance`, tolerance 7 days | 5 d 1 h 30 m ≤ 7 d → same as `RunImmediatelyOnce`. |
| `RunIfWithinTolerance`, tolerance 2 days | 5 d 1 h 30 m > 2 d → same as `Skip`. |
| `RunIfWithinTolerance` without a tolerance | Rejected at registration (`JobConfigurationException`). |
| `RescheduleFromNow` | **Rejected at registration** — monthly is a calendar schedule; re-anchoring "the 5th of the month" to an arbitrary instant would be a different schedule. |

The misfire is logged at `Warning` (*"Misfire detected: occurrence 2026-10-05T07:30:00.0000000+00:00
was missed (late by 5.01:30:00); applying policy …"*) and counted by `workerkit.job.misfires`.

## What if the host is down for a whole month?

There is **no backfill**. Only the most recently missed occurrence is ever considered, so:

- Down for all of September (back on 2026-09-20, before the October occurrence): the missed
  occurrence is `2026-09-05`. With `Skip` it is dropped and the job next runs on 2026-10-05; with
  `RunImmediatelyOnce` it runs once on 2026-09-20 carrying `ScheduledAtUtc = 2026-09-05T07:30Z`.
- Down for two months (back on 2026-10-10, the scenario above): September is dropped in **every**
  policy. At best, October runs late.

If your job must process every period regardless of downtime, do not encode the period as "the
current occurrence". Derive the work from durable state instead — read the last processed period
from `context.Checkpoints`, process every period from there up to `context.ScheduledLocalTime`, and
save the checkpoint after each period commits. The schedule then only decides *when* to look, not
*what* is owed, and a two-month outage costs you a late run rather than a missing invoice run.

## Implementation notes

- `MonthlySchedule` scans forward at most 26 months and `LastDayOfMonthSchedule` at most 3 when
  looking for the next occurrence; with any valid configuration a valid month is found within two
  steps, so these bounds are only guards against an internal bug (they throw
  `InvalidOperationException` with an explicit "this is a bug" message).
- Both schedules implement `IJobSchedule` and are pure functions of `(afterUtc, context)` — you can
  assert their behavior directly in unit tests without a host, exactly as
  `tests/ResilientWorkerKit.UnitTests/Scheduling/MonthlyScheduleTests.cs` does.

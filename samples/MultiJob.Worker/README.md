# MultiJob.Worker (sample)

Six independent jobs with six different schedule types in a single Worker Service host, using the
default in-memory stores. The point of this sample is scheduling variety and failure isolation;
for durability, checkpoints and resume see
[`ReservationReconciliation.Worker`](../ReservationReconciliation.Worker).

```bash
dotnet run --project samples/MultiJob.Worker
```

| Job | Schedule | Behavior |
|---|---|---|
| `heartbeat` | every 10 seconds + on startup | Always succeeds — the control group |
| `flaky-import` | 15 seconds after each completion | Fails on attempts 1 and 2 of every execution, succeeds on attempt 3 |
| `daily-digest` | daily 02:00 Europe/Istanbul | Calendar schedule with a time zone |
| `weekly-cleanup` | Sundays 03:00 Europe/Istanbul | Weekly schedule |
| `monthly-invoice` | day 5, 10:30 Europe/Istanbul (`SkipMonth`) | One occurrence per month, `RunImmediatelyOnce` misfire policy |
| `end-of-month-summary` | last day of month, 23:00 Europe/Istanbul | Correct across 28/29/30/31-day months |

## What to watch in the logs

Within the first minute you will see, without touching anything:

- `heartbeat` completing every 10 seconds, uninterrupted;
- `flaky-import` logging a warning per failed attempt, then
  `Retry succeeded on attempt 3` — with the **same ExecutionId** across all three attempts;
- the host staying alive throughout.

The calendar jobs log their next occurrence at Debug level; raise the log level in
`appsettings.json` to see them:

```json
{ "Logging": { "LogLevel": { "ResilientWorkerKit": "Debug" } } }
```

Because this sample uses in-memory stores, all history and health state is lost when the process
exits — that is exactly why the in-memory stores are documented as test/demo only.

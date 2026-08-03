# ResilientWorkerKit

A lightweight reliability and execution layer for custom .NET `BackgroundService` jobs:
scheduling, retry with failure classification, durable checkpoints, idempotency, failure
isolation and observability — without adopting a full job framework.

The contract is **at-least-once execution + durable checkpoints + idempotent processing**.
Exactly-once is never claimed; the docs explain why, and what to do instead.

## Quick start

```csharp
builder.Services.AddResilientWorkerKit(kit =>
{
    kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
        .WithFixedDelay(TimeSpan.FromMinutes(5))
        .WithRetry(r => { r.MaxRetries = 3; r.BaseDelay = TimeSpan.FromSeconds(5); })
        .WithTimeout(TimeSpan.FromMinutes(2))
        .PreventOverlappingExecutions());
});
```

```csharp
public sealed class ReservationSyncJob : IWorkerJob
{
    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken ct)
    {
        // scoped DI, typed checkpoints, idempotency keys, dead letters, safe logging
    }
}
```

## What it gives you

- **Scheduling:** interval, fixed delay, cron, daily/weekly/monthly, last-day-of-month,
  one-time, explicit future instants (`AtTimes`, `Repeating`), run-on-startup — with explicit
  DST handling, misfire policies and deterministic occurrence identity.
- **Failure handling:** retry with failure classification, attempt/total timeouts, overlap
  policies, dead letters, and `RetryLater` — durable follow-up retries executed under a
  revocable lease, so a crash or redeploy cannot lose a planned action.
- **State:** typed checkpoints and idempotency records so restarts resume instead of redoing.
- **Isolation:** a job exception can reach neither the scheduler loop nor the host.
- **Observability:** structured logs with stable event ids, metrics and traces via BCL
  primitives — no adapter package.

Durable persistence lives in
[ResilientWorkerKit.EntityFrameworkCore](https://www.nuget.org/packages/ResilientWorkerKit.EntityFrameworkCore);
the in-memory stores in this package are for tests and demos.

## Honest limitations

Single active host instance (job locking is in-process; only the pending-occurrence lease
capability is multi-host-safe). No exactly-once. No dashboard. All documented in
[limitations](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/docs/limitations.md).

## Links

[Repository](https://github.com/mFurkanHiz/ResilientWorkerKit) ·
[Documentation](https://github.com/mFurkanHiz/ResilientWorkerKit/tree/main/docs) ·
[Changelog](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/CHANGELOG.md) ·
MIT licensed · `net10.0` and `net8.0`

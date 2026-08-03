# ResilientWorkerKit.EntityFrameworkCore

Entity Framework Core persistence for
[ResilientWorkerKit](https://www.nuget.org/packages/ResilientWorkerKit): durable execution
history, checkpoints, idempotency records, dead letters and the lease-based
pending-occurrence queue behind `RetryLater`.

Verified against **SQLite** everywhere and against **SQL Server** in CI on every push (a
store contract suite runs on a real server — the compatibility claim is evidence, not design
intent). The model is provider-agnostic relational EF Core.

## Quick start

```csharp
builder.Services.AddResilientWorkerKit(kit =>
{
    kit.UseEntityFrameworkCore(db => db.UseSqlite(connectionString));
    // or db.UseSqlServer(connectionString)

    kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
        .WithFixedDelay(TimeSpan.FromMinutes(5)));
});
```

Five tables (`WorkerKitExecutions`, `WorkerKitCheckpoints`, `WorkerKitIdempotencyRecords`,
`WorkerKitDeadLetters`, `WorkerKitPendingOccurrences`); the host application owns migrations.
`AutoCreateSchema` exists for demos and tests. All timestamps are UTC `DateTime` columns.

## What the lease queue guarantees

A durably planned occurrence (a `RetryLater` follow-up) is executed under a revocable lease:
of any number of hosts, the database picks a single winner; a crashed owner's lease expires
and the occurrence re-delivers; completion deletes the row only after the outcome is durably
recorded. That makes *this queue* safe for multiple hosts sharing one database — the engine
as a whole still assumes a single active instance (see
[limitations](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/docs/limitations.md)).

**Upgrading from 1.1.x?** The 2.0 schema adds lease columns and a unique index that requires
a deduplication step first — exact SQL in
[the persistence guide](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/docs/persistence.md).

## Links

[Repository](https://github.com/mFurkanHiz/ResilientWorkerKit) ·
[Persistence guide](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/docs/persistence.md) ·
[Changelog](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/CHANGELOG.md) ·
MIT licensed · `net10.0` and `net8.0`

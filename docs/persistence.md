# Persistence

All durable state flows through five store interfaces (`IJobCheckpointStore`,
`IJobExecutionStore`, `IIdempotencyStore`, `IDeadLetterStore`, `IPendingOccurrenceStore`).
Two implementations ship:

| Provider | Package | Durability | Use for |
|---|---|---|---|
| In-memory | `ResilientWorkerKit` (registered by default) | none — lost on process exit | tests, samples, demos |
| EF Core | `ResilientWorkerKit.EntityFrameworkCore` | relational database | **everything else** |

## In-memory stores: tests and demos only

`AddResilientWorkerKit` registers `InMemoryJobCheckpointStore`, `InMemoryJobExecutionStore`,
`InMemoryIdempotencyStore`, `InMemoryDeadLetterStore` and `InMemoryPendingOccurrenceStore` with
`TryAddSingleton`. They are correct, thread-safe (`ConcurrentDictionary`, with `TryAdd`/`TryUpdate`
used to settle idempotency races) and **not suitable for production**, as each type's own XML
documentation states.

What you lose by keeping them:

- Checkpoints vanish on restart, so a resumable sync job starts over from the beginning.
- Execution history vanishes, so the `ScheduledExecutionId` deduplication that prevents a monthly
  job from running twice in one month cannot see the previous run — and misfire recovery has no
  record to check against.
- Idempotency records vanish, so side effects that were suppressed before a restart happen again.
- Dead letters vanish, so quarantined items are silently lost.
- Startup recovery has nothing to mark `Abandoned`.
- Queued follow-up retries vanish, which defeats the entire point of `RetryLater`: its promise is
  that a planned action still happens after a restart.

They are the right choice for unit tests and for the `MultiJob.Worker` sample, which demonstrates
scheduling and failure isolation rather than durability.

`InProcessJobLockProvider` is in the same category but for a different reason: it is a per-process
lock, sufficient for the single-active-instance deployment model, and it is not replaced by the EF
Core package.

## EF Core registration

`UseEntityFrameworkCore` is called inside the `AddResilientWorkerKit` callback. It registers
`AddDbContextFactory<WorkerKitDbContext>` plus the five EF Core stores as singletons, overriding the
in-memory defaults.

### SQLite

```csharp
builder.Services.AddResilientWorkerKit(kit =>
{
    kit.UseEntityFrameworkCore(
        db => db.UseSqlite(builder.Configuration.GetConnectionString("WorkerKit")
                           ?? "Data Source=reservation-reconciliation.db"),
        ef => ef.AutoCreateSchema = true);   // demo convenience only — see Schema management

    kit.AddJob<ReservationSyncJob>("reservation-sync", job => job
        .WithInterval(TimeSpan.FromMinutes(5))
        .RunOnStartup());
});
```

SQLite connection strings contain no credentials, so they are safe in `appsettings.json`.

### SQL Server

```csharp
kit.UseEntityFrameworkCore(db => db.UseSqlServer(
    builder.Configuration.GetConnectionString("WorkerKit"),
    sql => sql.EnableRetryOnFailure()));
```

```jsonc
// appsettings.json — no password, ever
{
  "ConnectionStrings": {
    "WorkerKit": "Server=sql.internal;Database=WorkerKit;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False"
  }
}
```

If the environment forces SQL authentication, keep the whole connection string in a secret store or
environment variable (`ConnectionStrings__WorkerKit`) and let configuration binding supply it. The
same rule as the HTTP package: secrets come from configuration or a secret store, never from source.

The stores use `IDbContextFactory`, creating and disposing one short-lived `DbContext` per
operation. They are safe to use concurrently from every job loop, and they never hold a context
across an `await` that spans job code.

## Schema

Five tables. All string lengths below are the configured maximums; a column with no length is
mapped to the provider's unbounded text type.

### `WorkerKitExecutions`

Primary key: `ExecutionId`. Indexes: `(JobId, StartedAtUtc)`, `(JobId, ScheduledExecutionId)`,
`(Status)`.

| Column | Type | Max length | Notes |
|---|---|---|---|
| `ExecutionId` | string | 64 | **PK**; stable across retry attempts |
| `JobId` | string | 200 | required |
| `ScheduledExecutionId` | string | 300 | required; `{jobId}:{identityToken}` |
| `ScheduledAtUtc` | DateTime | | UTC |
| `ScheduledLocalTime` | DateTime? | | planned time in the job's zone |
| `TimeZoneId` | string? | 100 | |
| `TriggerType` | string | 32 | required; `schedule`/`startup`/`misfire`/`queued-overlap`/`manual` |
| `StartedAtUtc` | DateTime | | UTC |
| `CompletedAtUtc` | DateTime? | | null while running |
| `Status` | enum → string | 20 | `Running`/`Completed`/`Failed`/`Cancelled`/`TimedOut`/`Abandoned` |
| `FailureKind` | enum? → string | 20 | `Transient`/`Permanent`/`Cancelled`/`TimedOut`/`Abandoned`/`Misconfigured` |
| `AttemptCount` | int | | |
| `DurationMs` | double? | | first attempt start → completion |
| `ErrorType` | string? | 300 | exception type full name |
| `ErrorMessage` | string? | 500 | truncated exception message |
| `ErrorDetail` | string? | 4000 | truncated `exception.ToString()` (stack trace) |
| `CorrelationId` | string? | 64 | equals the `ExecutionId` |
| `HostInstanceId` | string? | 200 | `{machine}:{pid}` by default |
| `LastCheckpointSummary` | string? | 500 | type name + first 160 chars of checkpoint JSON |
| `CreatedAtUtc` | DateTime | | UTC |
| `UpdatedAtUtc` | DateTime | | UTC |

Enums are stored as strings (`HasConversion<string>`), so a dump of this table is readable without
the assembly and adding an enum member does not renumber history.

### `WorkerKitCheckpoints`

Primary key: `JobId`. One row per job — the table never grows with time.

| Column | Type | Max length | Notes |
|---|---|---|---|
| `JobId` | string | 200 | **PK** |
| `PayloadJson` | string | unbounded | required; opaque JSON owned by job code |
| `PayloadType` | string? | 300 | diagnostics only |
| `UpdatedAtUtc` | DateTime | | UTC |

### `WorkerKitIdempotencyRecords`

Primary key: **composite** `(JobId, Key)`. Index: `(ExpiresAtUtc)`.

| Column | Type | Max length | Notes |
|---|---|---|---|
| `JobId` | string | 200 | **PK part 1** |
| `Key` | string | 400 | **PK part 2**; a stable business identity |
| `Status` | enum → string | 20 | `Pending`/`Completed`/`Failed` |
| `ExecutionId` | string? | 64 | the execution holding the key |
| `CreatedAtUtc` | DateTime | | UTC |
| `CompletedAtUtc` | DateTime? | | set when marked completed |
| `ExpiresAtUtc` | DateTime? | | null = no expiry |
| `Version` | int | | **optimistic-concurrency token** |

### `WorkerKitDeadLetters`

Primary key: `Id`. Index: `(JobId, CreatedAtUtc)`.

| Column | Type | Max length | Notes |
|---|---|---|---|
| `Id` | string | 64 | **PK** |
| `JobId` | string | 200 | required |
| `ExecutionId` | string | 64 | required |
| `Scope` | string | 16 | required; `execution` or `item` |
| `ItemId` | string? | 300 | safe item identifier, e.g. `reservation:41` |
| `FailureKind` | enum? → string | 20 | |
| `Reason` | string | 1000 | required; sanitized |
| `AttemptCount` | int | | |
| `PayloadSummary` | string? | 2000 | masked summary — never the raw payload |
| `CreatedAtUtc` | DateTime | | UTC |
| `ReprocessedAtUtc` | DateTime? | | null = still pending |

### `WorkerKitPendingOccurrences`

Occurrences planned durably rather than derived from a schedule. Today that means follow-up
retries (`RetryLater`); the shape is deliberately general so runtime-created triggers can reuse
the table without a schema change.

Primary key: `Id`. Indexes: `(JobId, DueAtUtc)` — the scheduler asks for "the earliest pending
occurrence for this job" on every loop iteration — and a **unique** `(JobId, IdentityToken)`,
which makes the database the arbiter of "this logical occurrence is already queued": two
processes planning the same follow-up cannot double-queue it.

| Column | Type | Max length | Notes |
|---|---|---|---|
| `Id` | string | 64 | **PK**; opaque |
| `JobId` | string | 200 | required; unique with `IdentityToken` |
| `DueAtUtc` | DateTime | | UTC; when it becomes runnable |
| `IdentityToken` | string | 300 | required; e.g. `at:2026-08-15T07:00:00Z+followup-1` |
| `Source` | string | 32 | required; `follow-up-retry` |
| `OriginScheduledExecutionId` | string? | 300 | the occurrence being retried |
| `FollowUpOrdinal` | int | | 1-based |
| `PayloadJson` | string? | | **write-only today**: stored and carried, read by nothing. Reserved for future runtime triggers, which will define their own schema-versioned, size-limited, validated payload model — this column is not that feature. Must never contain secrets or personal data. |
| `CreatedAtUtc` | DateTime | | UTC |
| `LeaseOwner` | string? | 200 | host holding the lease; null = unleased |
| `LeaseToken` | string? | 64 | proof of ownership, known only to the acquirer |
| `ClaimedAtUtc` | DateTime? | | UTC; when the lease was acquired |
| `LeaseExpiresAtUtc` | DateTime? | | UTC; past this instant the row is acquirable again |

**Execution is under a lease, and only completion deletes.** 1.x claimed a row by deleting it,
which left a window — after the delete, before the first durable execution record — where a
crash lost the planned action permanently. Now:

1. `TryAcquireLeaseAsync` is a conditional `UPDATE` (`WHERE` unleased-or-expired); the rows-
   affected count makes the database pick a single winner, and the returned token is the proof
   of ownership every later operation must present.
2. While the execution runs, the engine renews the lease at a third of its duration
   (`WorkerKitOptions.PendingOccurrenceLeaseDuration`, default 5 minutes), so a live run never
   loses its lease.
3. `CompleteAsync` — a conditional `DELETE` on `(Id, LeaseToken)` — runs only after the
   execution outcome is durably recorded *and* any next follow-up is durably queued. If the
   process dies anywhere before that, the lease expires and the occurrence is delivered again.
4. Recovery is **visibility-based**: `GetNextAsync` surfaces a leased row no earlier than its
   lease expiry, so a scheduler sleeps exactly until a dead owner's work becomes acquirable —
   no background sweeper, no polling loop.

This makes the pending-occurrence capability itself safe for multiple hosts sharing one
database (single lease winner, expiry takeover, owner-checked complete/release — the store
contract test suite runs identically against the in-memory store, SQLite and SQL Server). It
does **not** make the engine as a whole multi-instance safe; see
[limitations.md](limitations.md).

Two operational notes:

- **Clock discipline.** Stores never read a clock; `nowUtc` is always a parameter supplied
  from the host's `TimeProvider`. Hosts sharing one database must keep their clocks within a
  third of the lease duration (the renewal interval) of each other — with the 5-minute
  default, ordinary NTP is far more than enough.
- **Re-delivery is at-least-once by contract.** A crash mid-execution means the same
  occurrence runs again after expiry. The completed-identity check prevents re-running
  anything that recorded completion; everything else is the documented at-least-once model,
  which is why job bodies must be idempotent.

Rows are removed on completion, so the table stays small: it holds only occurrences that are
waiting or in flight, never history. A follow-up that ran leaves its trace in
`WorkerKitExecutions` instead.

#### Migrating from 1.1.x

The 2.0 schema adds the four nullable lease columns and the unique `(JobId, IdentityToken)`
index. 1.1.x could legitimately hold duplicate `(JobId, IdentityToken)` pairs (its
lock-declined path re-inserted rows under fresh ids), so **deduplicate before creating the
unique index** — keep the earliest row per pair:

```sql
ALTER TABLE WorkerKitPendingOccurrences ADD LeaseOwner nvarchar(200) NULL;
ALTER TABLE WorkerKitPendingOccurrences ADD LeaseToken nvarchar(64) NULL;
ALTER TABLE WorkerKitPendingOccurrences ADD ClaimedAtUtc datetime2 NULL;
ALTER TABLE WorkerKitPendingOccurrences ADD LeaseExpiresAtUtc datetime2 NULL;

DELETE p FROM WorkerKitPendingOccurrences p
JOIN (
    SELECT JobId, IdentityToken, MIN(CreatedAtUtc) AS KeepCreatedAtUtc
    FROM WorkerKitPendingOccurrences
    GROUP BY JobId, IdentityToken
    HAVING COUNT(*) > 1
) d ON d.JobId = p.JobId AND d.IdentityToken = p.IdentityToken
WHERE p.CreatedAtUtc > d.KeepCreatedAtUtc;

CREATE UNIQUE INDEX IX_WorkerKitPendingOccurrences_JobId_IdentityToken
    ON WorkerKitPendingOccurrences (JobId, IdentityToken);
```

(SQL Server syntax; adapt types for other providers — SQLite demos using
`AutoCreateSchema` recreate the schema and need nothing.) EF Core migration users get the same
result from `dotnet ef migrations add`, but must add the deduplication `DELETE` to the
generated migration by hand — the tooling cannot know which duplicate to keep.

## Why timestamps are `DateTime`, not `DateTimeOffset`

Every timestamp column is a UTC `DateTime`, even though the public abstractions
(`JobExecutionRecord`, `JobCheckpoint`, `IdempotencyRecord`, `DeadLetterRecord`) expose
`DateTimeOffset`. The conversion happens at the mapping boundary inside the stores.

The reason is SQLite: it cannot `ORDER BY` or compare `DateTimeOffset` columns, and the store does
exactly that in its hot paths —

```csharp
db.Executions.Where(e => e.JobId == jobId).OrderByDescending(e => e.StartedAtUtc)
```

which powers `GetLatestAsync`, `GetRecentAsync` and the scheduler's restart-time state recovery.
Since every timestamp in the model is UTC by construction, the offset carries no information and
nothing is lost by dropping it.

What this means for consumers:

- Reading through the store interfaces, you always get `DateTimeOffset` with a zero offset. The
  conversion back is `new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))`.
- Reading the tables directly (SQL, BI tools, an admin page), treat every timestamp column as UTC.
  There is no local-time column except `ScheduledLocalTime`, which is the planned time in the job's
  own zone and is stored precisely so that a monthly report can show "10:30 Istanbul time" without
  re-deriving it.
- If you map these tables into your own `DbContext` or query them with Dapper, do not assume the
  `DateTime.Kind` your provider returns. SQLite returns `Unspecified`; specify `Utc` yourself.

## Idempotency: how the race is settled

The composite primary key `(JobId, Key)` *is* the guarantee. Two executions that try to acquire the
same key at the same moment both attempt an `INSERT`; the database rejects one of them with a
constraint violation, and the loser re-reads and re-evaluates. The decision is made by the database
engine, not by C# — there is no window between "check" and "insert" for a second thread to slip
into.

`EfCoreIdempotencyStore.TryAcquireAsync` loops up to three times:

1. **No row** → insert `Pending`. Success → `Acquired`. `DbUpdateException` (lost the insert race)
   → retry the loop.
2. **Row exists, not expired, `Completed`** → `AlreadyCompleted` (the caller skips the item).
3. **Row exists, not expired, `Pending`** → `Acquired` if the row's `ExecutionId` matches the
   caller's (the same execution re-entering, e.g. after a job-level retry), otherwise
   `InProgressElsewhere`.
4. **Row exists but is `Failed` or expired** → re-acquire in place: set `Pending`, take ownership,
   reset `CompletedAtUtc`, and increment `Version`. Because `Version` is a concurrency token, EF
   includes its original value in the `WHERE` clause, so exactly one concurrent re-acquire commits;
   the other gets `DbUpdateConcurrencyException` and retries the loop.

After three failed rounds the method returns `InProgressElsewhere` — a safe answer, since it makes
the caller skip rather than double-process.

`EfCorePersistenceTests.ConcurrentIdempotencyAcquires_ExactlyOneWinner_SettledByTheDatabase` fires
16 concurrent acquisitions of one key at a real SQLite database and asserts exactly 1 `Acquired` and
15 `InProgressElsewhere`.

`MarkCompletedAsync` and `MarkFailedAsync` also increment `Version`. They perform a single update
without an internal retry, so a genuinely concurrent status change surfaces as
`DbUpdateConcurrencyException` from the store call; job code should not be marking the same key from
two places at once.

An **expired record behaves as if absent** in both `TryAcquireAsync` and `ExistsCompletedAsync` —
but it is not deleted (see [Retention](#retention-and-growth)). Set the TTL per job with
`job.WithIdempotencyTimeToLive(...)`.

## Schema management

### `AutoCreateSchema` — demo and test only

```csharp
kit.UseEntityFrameworkCore(db => db.UseSqlite(cs), ef => ef.AutoCreateSchema = true);
```

Registers `WorkerKitSchemaInitializer`, an `IHostedService` that calls
`Database.EnsureCreatedAsync()` at startup and logs once at Information when it actually created the
schema.

Ordering is guaranteed, not incidental: `AddResilientWorkerKit` always registers the engine's
hosted service **last**, after the callback has run. Anything you register from inside the
callback to prepare durable state — this initializer, your own migration runner — therefore
starts before the first job does. Registering a migration runner *outside* the callback, after
`AddResilientWorkerKit` returns, puts it behind the engine and is a mistake:

```csharp
services.AddResilientWorkerKit(kit =>
{
    kit.UseEntityFrameworkCore(db => db.UseSqlServer(cs));
    kit.Services.AddHostedService<MyMigrationRunner>();   // correct: runs before the engine
    kit.AddJob<MyJob>(...);
});

services.AddHostedService<MyMigrationRunner>();           // wrong: runs after the engine starts
```

A store that is not ready when the engine starts does not corrupt anything — store failures are
caught and logged, and the affected execution fails transiently and is retried — but the first
executions will fail for no useful reason.

`EnsureCreated` has no versioning: it creates the schema if the database has no tables and does
nothing otherwise. It will never apply a change to an existing database, and it does not coexist
with migrations. `AutoCreateSchema` defaults to `false` and should stay false in production.

### EF Core migrations — production

The kit ships no migrations, because migrations are provider-specific and the host application owns
its database. Generate them against `WorkerKitDbContext` from your own project.

The design-time tools discover the context through the `AddDbContextFactory<WorkerKitDbContext>`
registration that `UseEntityFrameworkCore` performs, so pointing `--startup-project` at your worker
is enough:

```bash
dotnet tool install --global dotnet-ef
dotnet add src/YourApp package Microsoft.EntityFrameworkCore.Design

dotnet ef migrations add InitialWorkerKit \
  --project src/YourApp \
  --startup-project src/YourApp \
  --context WorkerKitDbContext \
  --output-dir Migrations/WorkerKit

dotnet ef database update --context WorkerKitDbContext --startup-project src/YourApp
```

For deployments that apply SQL rather than running `database update` against production:

```bash
dotnet ef migrations script --idempotent \
  --context WorkerKitDbContext \
  --startup-project src/YourApp \
  --output artifacts/workerkit.sql
```

If your host cannot be started at design time (a `WebApplication` with required configuration, for
example), add a design-time factory next to your composition root:

```csharp
public sealed class WorkerKitDbContextFactory : IDesignTimeDbContextFactory<WorkerKitDbContext>
{
    public WorkerKitDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkerKitDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=WorkerKit-Design")
            .Options;
        return new WorkerKitDbContext(options);
    }
}
```

The connection string here is only used to pick a provider and generate DDL — point it at a local
development database, never at production, and keep it credential-free.

### Embedding the model in your own `DbContext`

If your application prefers a single context and a single migration history, apply the model to it:

```csharp
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWorkerKitModel();   // the five WorkerKit* tables
    }
}
```

`WorkerKitModelBuilderExtensions.ApplyWorkerKitModel` is the same call `WorkerKitDbContext` makes in
its own `OnModelCreating`, so both models describe byte-identical tables.

One thing to be clear about: the EF Core stores resolve `IDbContextFactory<WorkerKitDbContext>` and
will keep doing so. Embedding the model does not move the runtime onto your context — it makes
*your* migrations the owner of the schema while both contexts read and write the same tables in the
same database. Point them at the same connection string, keep `AutoCreateSchema` false, and let your
application's migrations create the tables.

## Provider compatibility

The model uses only portable constructs: `string`/`int`/`double`/`DateTime` columns, maximum
lengths, a composite primary key, non-unique indexes, string-converted enums and an `int`
concurrency token. Nothing is provider-specific — no computed columns, no sequences, no JSON columns,
no `rowversion`.

- **SQLite** — the tested provider (`EfCorePersistenceTests` and the reservation sample run against
  a real SQLite file). It is also the reason timestamps are `DateTime`.
- **SQL Server** — supported by construction. `Version` as an `int` concurrency token works through
  EF's `WHERE Version = @original` comparison; no `rowversion` column is required.
- **PostgreSQL** — no changes are needed in the kit. Add `Npgsql.EntityFrameworkCore.PostgreSQL`,
  call `db.UseNpgsql(...)`, and generate a Npgsql-specific migration (migrations cannot be shared
  between providers). Npgsql maps `DateTime` to `timestamp with time zone` and requires
  `DateTimeKind.Utc` on write: the stores always write `DateTimeOffset.UtcDateTime`, whose `Kind` is
  `Utc`, and re-specify `Utc` on read, so that requirement is already satisfied. Verify the
  behaviour with the same scenarios `EfCorePersistenceTests` covers — in particular the concurrent
  acquire test, which is the one that exercises provider-specific constraint-violation exceptions.
- Other relational providers should work on the same basis; the store code catches
  `DbUpdateException` and `DbUpdateConcurrencyException`, which every relational provider raises.

## Retention and growth

Nothing in the kit deletes rows. Plan retention yourself.

| Table | Growth | Bounded by |
|---|---|---|
| `WorkerKitExecutions` | one row per execution | nothing |
| `WorkerKitIdempotencyRecords` | one row per distinct key | nothing (`ExpiresAtUtc` marks a row *inert*, not deleted) |
| `WorkerKitDeadLetters` | one row per dead-lettered item or exhausted execution | nothing |
| `WorkerKitCheckpoints` | one row per job | the number of registered jobs |
| `WorkerKitPendingOccurrences` | one row per waiting follow-up | self-limiting: rows are deleted when claimed |

Order of magnitude: a job on a one-minute schedule writes 1,440 execution rows a day, about 525,000
a year. A sync job that acquires an idempotency key per item writes one row per item per version.

Practical approach:

- **Executions.** Keep enough history for the invariants that read it. The scheduler reads the 200
  most recent records per job at startup, and `ExistsForScheduledExecutionAsync` must still find the
  occurrence identity of the last calendar period — so a monthly job needs at least a few months of
  history to keep its "already ran this month" guarantee across a restart. Deleting rows younger
  than that can cause a duplicate run. A retention window comfortably longer than the longest
  schedule period, purged on `(JobId, StartedAtUtc)`, is the safe shape.
- **Idempotency records.** Give each job a TTL (`WithIdempotencyTimeToLive`) longer than the window
  in which a duplicate could plausibly be re-delivered, then purge on the `ExpiresAtUtc` index —
  that index exists for exactly this query. Rows with `ExpiresAtUtc IS NULL` are permanent by
  design; use them only for keys that must never be reprocessed.
- **Dead letters.** These are a work queue for humans. Purge on `ReprocessedAtUtc IS NOT NULL` after
  an audit window; a growing count of rows with `ReprocessedAtUtc IS NULL` is an alert, not a
  retention problem (see [observability.md](observability.md)).

A cleanup job written with the kit itself is a reasonable place for this — the reservation sample's
`weekly-cleanup` registration is the shape.

## What must never be written to these tables

The tables are operational metadata. They are backed up, replicated, read by support staff and
exported into dashboards, and the kit applies no encryption and (outside of the HTTP package's
`ApiRequestException`) no masking.

Never store:

- **Secrets and credentials** — API keys, bearer or refresh tokens, connection strings, signatures.
- **Raw API request or response bodies** — in a checkpoint payload, a dead-letter `PayloadSummary`,
  or an exception message.
- **Personal data** — names, e-mail addresses, phone numbers, addresses, national identifiers. This
  includes idempotency keys: use `reservation:41:v7`, never `booking:jane.doe@example.com`.
- **Anything you would not paste into a support ticket.**

Two places deserve specific attention because they are filled automatically:

- `ErrorMessage` (500 chars) and `ErrorDetail` (4,000 chars) are written from
  `exception.Message` and `exception.ToString()` **verbatim**, truncated but not masked. If job code
  throws an exception whose message interpolates a token, a URL with a query string or a customer
  record, that value lands in the database. This is precisely why the HTTP package's
  `EnsureApiSuccessAsync` builds a message from method, authority, path, status and ProblemDetails
  `title` only — reuse that discipline in your own exceptions, and run untrusted upstream text
  through `SensitiveDataMasker.MaskSecrets` before it becomes an exception message.
- `LastCheckpointSummary` (500 chars) contains the checkpoint type name plus the **first 160
  characters of the serialized checkpoint JSON**. Checkpoint state should be positional — a
  continuation token, a page number, a high-water timestamp — never a buffer of records.

`DeadLetterRecord.PayloadSummary` is documented as a masked summary or a reference. Store an
identifier that lets an operator find the payload in the system that owns it, not the payload.

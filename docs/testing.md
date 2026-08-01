# Testing

Two things matter here: how ResilientWorkerKit itself is tested, and how you test *your* jobs.

## How the kit is tested

| Suite | What it exercises |
|---|---|
| `ResilientWorkerKit.UnitTests` | Pure schedule math, retry math, failure classification, the runner and the schedule loop against in-memory stores with a fake clock, HTTP handlers, health evaluation, registration validation |
| `ResilientWorkerKit.IntegrationTests` | A real Generic Host, real DI scopes, a real SQLite file that survives host restarts, and a real HTTP server |

Both projects multi-target `net10.0` and `net8.0`, so every test runs twice — once per supported
framework, against framework-matched dependency versions. Claiming a target framework without
executing anything on it is not support.

Counts and coverage are deliberately not repeated here: the
[CI runs](https://github.com/mFurkanHiz/ResilientWorkerKit/actions/workflows/ci.yml) are the
source of truth, and a number typed into a document is only correct until the next commit.

```bash
dotnet test                                          # everything
dotnet test tests/ResilientWorkerKit.UnitTests       # fast (~3 s)
dotnet test tests/ResilientWorkerKit.IntegrationTests
```

### Principles applied

**No real waiting.** Every schedule and backoff computation runs on `FakeTimeProvider`; a month of
scheduling is verified in milliseconds. There is no `Thread.Sleep` anywhere, and the only real
delays are short polling loops with hard timeouts in the integration suite.

**Schedules are pure functions.** `IJobSchedule.GetOccurrenceAfter` takes the current time as an
input rather than reading a clock, so DST gaps, ambiguous hours, leap years and month-length edge
cases are ordinary table tests:

```csharp
[Theory]
[InlineData(2026, 2, 1, 2026, 2, 28)]   // regular February
[InlineData(2028, 2, 1, 2028, 2, 29)]   // leap-year February
[InlineData(2026, 4, 10, 2026, 4, 30)]  // 30-day month
public void FiresOnTheActualLastDay(...) { }
```

**The engine is tested through its real seams.** `RunnerHarness` builds a genuine `JobRunner` over
in-memory stores and a delegate job; `LoopHarness` drives a genuine `JobScheduleLoop` with a fake
clock. Neither mocks the component under test.

**Failure isolation is asserted, not assumed.** There are tests for "a store that throws on every
call still lets the execution complete", "a failing job's loop keeps scheduling", and "two loops
are independent".

**The integration suite is allowed to find real bugs — and did.** The SQLite `ORDER BY
DateTimeOffset` limitation was caught by the end-to-end restart test, not by review; the EF Core
model now persists UTC `DateTime` because of it.

### The flagship scenario

`EndToEndResumeTests.Sync_FailsOnPage2_HostSurvives_ThenResumesFromCheckpointAfterRestart` runs
the whole contract in one test:

1. Host starts against a temp SQLite file; the paged sync job processes page 1 and checkpoints.
2. The fake API fails page 2 with HTTP 500; the engine retries and exhausts them.
3. The execution is recorded `Failed` with `AttemptCount = 3`, an execution-level dead letter is
   written, and the recorded error message contains the status and path but **not** the query
   string.
4. The host survives; the unrelated job keeps completing on its own schedule.
5. The host stops gracefully. A **new** host starts against the **same database**.
6. The sync job resumes at page 2, and the item that was already applied before the crash produces
   no second side effect — only the genuinely new item does.

## Testing your own jobs

### Level 1 — call `ExecuteAsync` directly

A job is a plain class. Build a context and call it:

```csharp
[Fact]
public async Task SkipsItemsThatWereAlreadyProcessed()
{
    var idempotency = new InMemoryIdempotencyStore();
    await idempotency.TryAcquireAsync("my-job", "reservation:41:v7", "exec-0", null);
    await idempotency.MarkCompletedAsync("my-job", "reservation:41:v7");

    var ledger = new FakeLedger();
    var job = new ReservationSyncJob(new FakeApiClient(), ledger);

    await job.ExecuteAsync(TestContext.For("my-job", idempotency: idempotency), CancellationToken.None);

    Assert.Empty(ledger.Applied);
}
```

`JobExecutionContext` has settable init properties, so a small factory in your test project can
build one over the in-memory stores. Copy `RunnerHarness` from this repository as a starting
point — it is ~60 lines.

### Level 2 — run the real engine

Register your job and drive the runner with a fake clock, exactly as this repository does. This is
the right level for asserting retry counts, checkpoint advancement and status outcomes, because
those are engine behaviors that a direct `ExecuteAsync` call cannot show.

### Level 3 — full host

For resume and restart behavior, use a real host and a temp SQLite file:

```csharp
using var database = new SqliteDatabase();          // a temp file, deleted on dispose
var host1 = await WorkerHost.StartAsync(database, kit => kit.AddJob<MyJob>(...));
// ... assert, then dispose to stop the host ...
var host2 = await WorkerHost.StartAsync(database, kit => kit.AddJob<MyJob>(...));
// ... assert the resume behavior ...
```

`WorkerHost`, `SqliteDatabase` and `FakeApiServer` in
`tests/ResilientWorkerKit.IntegrationTests/Infrastructure/` are ~250 lines total and designed to
be copied into your own repository.

### Testing HTTP behavior

Prefer a real in-process HTTP server (`HttpListener`, as `FakeApiServer` does) over mocking
`HttpMessageHandler`: it exercises the actual resilience pipeline, real status codes and real
headers, including `Retry-After`.

If you want every HTTP failure to become a visible *job* attempt in the execution record, switch
the HTTP-level retry off for that client:

```csharp
o.ConfigureResilience = r => r.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
```

Otherwise the two retry layers multiply and your attempt counts will surprise you.

### What to assert

| Behavior | Assert on |
|---|---|
| Business logic | Your own domain state |
| "Did it retry?" | `JobExecutionRecord.AttemptCount` |
| "Did it fail the right way?" | `Status` and `FailureKind` |
| "Did it resume?" | The checkpoint payload after the failing run, and the side effects after the second run |
| "Was the duplicate suppressed?" | A side-effect counter, not the idempotency table |
| "Is the job healthy?" | `IJobHealthTracker.Get(jobId)` |

### Common pitfalls

- **Waiting on "any completed execution"** when the test seeded a completed record — the wait
  passes instantly and asserts nothing. Wait for the specific `ScheduledExecutionId`.
- **A job body that waits forever on a token nobody cancels** — the test hangs. Give every
  blocking test job an exit path.
- **Real sleeps in schedule tests.** Use `FakeTimeProvider` and advance it.
- **Asserting on log text.** Assert on the execution record; logs are for humans.

## Coverage

CI collects coverage with coverlet and publishes a reportgenerator HTML artifact plus a summary in
the job output. Coverage numbers are reported as measured — this repository does not quote a
target it has not met.

# ResilientWorkerKit.Abstractions

The contracts of [ResilientWorkerKit](https://www.nuget.org/packages/ResilientWorkerKit):
job, schedule, checkpoint, idempotency, execution-history, pending-occurrence (lease),
locking and failure-classification abstractions for reliable .NET background jobs.

Reference this package when you:

- implement a job in a project that should not depend on the engine (`IWorkerJob`,
  `JobExecutionContext`);
- write a custom schedule (`IJobSchedule`) or failure classifier;
- implement a durable store for your own database
  (`IJobExecutionStore`, `IJobCheckpointStore`, `IIdempotencyStore`, `IDeadLetterStore`,
  `IPendingOccurrenceStore` — the last one is a lease contract: atomic single-winner
  acquisition, token-checked renew/complete/release, visibility-based expiry recovery).

Applications that just run jobs reference
[ResilientWorkerKit](https://www.nuget.org/packages/ResilientWorkerKit) instead, which brings
this package transitively.

The execution model these contracts encode is **at-least-once + durable checkpoints +
idempotent processing** — never exactly-once.

## Links

[Repository](https://github.com/mFurkanHiz/ResilientWorkerKit) ·
[Public API guide](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/docs/public-api.md) ·
[Changelog](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/CHANGELOG.md) ·
MIT licensed · `net10.0` and `net8.0`

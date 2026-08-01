# Security

## Threat model in one paragraph

ResilientWorkerKit runs inside your host process, talks to your database and (optionally) to
external APIs, and writes diagnostics. The realistic risks are therefore **secret leakage through
diagnostics**, **sensitive data landing in durable state**, and **misconfiguration that points a
job at the wrong environment**. Every rule below exists because the
[clean-room analysis](problem-analysis.md) found these exact failures in production worker code.

## Secrets

**The library never asks for a secret in code.** There is no `WithApiKey("...")` overload by
design; credentials arrive through the standard configuration/secret pipeline:

```csharp
// Program.cs — the value is resolved from configuration or a secret store, never a literal.
builder.Services.AddSingleton<IApiKeyProvider>(sp =>
    new ConfigurationApiKeyProvider(builder.Configuration["ReservationApi:ApiKey"]!));
```

Rules for the host application:

- Never commit credentials to `appsettings.json`, `appsettings.Development.json`, or any file in
  the repository. Use user secrets locally and environment variables / a secret manager in
  deployment.
- Never keep alternate environments' credentials as commented-out lines — rotation misses them.
- Never put credentials in URL query strings; they are recorded by proxies and access logs.
  The HTTP package's auth handlers use headers exclusively.
- Never select an environment with a compile-time constant. Configuration is the switch.

This repository ships [`.env.example`](../.env.example) and secret-free connection strings in the
samples. Nothing in it is a real credential.

## What the library logs

The engine's own logging is designed to be safe to ship to a central log store:

| Logged | Never logged |
|---|---|
| JobId, ExecutionId, ScheduledExecutionId, AttemptNumber, CorrelationId, HostInstanceId | Job payloads |
| Schedule type, scheduled/actual times, duration, result status | Checkpoint contents beyond a truncated summary |
| Exception type, message and stack trace (as an exception object, so nothing is lost) | Any header on the sensitive list |
| HTTP method, scheme, authority, **absolute path**, status code, duration, retry attempt | Request/response **bodies**, **query strings**, tokens, cookies |

The HTTP logging handler records metadata only. Sensitive headers (`Authorization`,
`Proxy-Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, `Api-Key`, `X-Auth-Token`,
`X-Amz-Security-Token`, plus anything you add via `AdditionalMaskedHeaders`) are never emitted,
and free-text passed through `SensitiveDataMasker.MaskSecrets` has bearer/basic credentials and
`key=value` style secrets replaced with `***`.

Error messages produced by `EnsureApiSuccessAsync` are assembled from the method, the
`scheme://authority` plus absolute path, the status code, and — when the response is a JSON
problem document — its `title`. The query string is deliberately excluded because it is where
tokens most often hide.

### Log injection

Every engine log call uses a **constant message template** with structured parameters; no
user-controlled string is ever used as a template. This prevents both format-placeholder
injection (`{` in a message throwing inside a catch block) and template-cardinality explosions.
Apply the same rule in your own jobs:

```csharp
context.Logger.LogInformation("Reconciled reservation {ReservationId}", reservation.Id); // correct
context.Logger.LogInformation($"Reconciled reservation {reservation.Id}");               // wrong
context.Logger.LogInformation(ex.Message);                                               // wrong twice
```

If a log value can contain newlines from an external source, treat your log pipeline as the
control: structured logging keeps the value inside a field rather than the message line.

## Durable state

Three stores hold data that outlives the process. Keep them boring:

| Store | Must not contain |
|---|---|
| **Checkpoints** | Secrets, tokens, personal data. Store *positions* (continuation token, last id, watermark), not records. If the upstream continuation token itself is a credential-bearing opaque blob, treat the checkpoint table as sensitive and restrict access accordingly. |
| **Idempotency keys** | Personal data. Use surrogate identities: `reservation:41:v7`, not `reservation:ada@example.com`. Keys are indexed, long-lived and frequently visible in diagnostics. |
| **Dead letters** | Raw payloads. The API takes a *summary*; mask before you pass it. The engine writes only sanitized failure messages for execution-level entries. |

Execution history stores sanitized error messages (500 chars) and stack traces (4000 chars). If
your exception messages embed payload content, sanitize at the throw site — the engine cannot know
which substring is sensitive.

## Transport and storage

- Enable TLS on database connections (`Encrypt=True` for SQL Server); the samples' SQLite files
  are local by definition.
- Restrict the worker's database principal to the tables it needs.
- Execution history and idempotency records grow monotonically; a retention job is a data-privacy
  measure as much as a storage one. See [persistence.md](persistence.md).

## Not exposing internals to end users

Stack traces and `ErrorDetail` are for operators. If you build an admin endpoint over
`IJobExecutionStore`, return `Status`, `FailureKind` and the sanitized `ErrorMessage`; keep
`ErrorDetail` behind an authorization check.

## Repository hygiene

- [`.gitignore`](../.gitignore) excludes `bin/`, `obj/`, `*.db`, `logs/`, `.env`, `secrets.json`
  and `appsettings.*.local.json`.
- CI runs `dotnet list package --vulnerable --include-transitive` and fails on findings.
- Packages are built deterministically with SourceLink so consumers can verify what they run.

Before publishing, scan the working tree — for example with
`git grep -inE "password|secret|api[_-]?key|token|connectionstring"` — and confirm every hit is a
documentation example, a masking rule, or a test literal.

## Supply chain

Direct dependencies are pinned centrally in [`Directory.Packages.props`](../Directory.Packages.props)
and limited to Microsoft-published packages plus [Cronos](https://github.com/HangfireIO/Cronos)
for cron parsing. Adding a dependency to the core package is a deliberate decision, not a
convenience.

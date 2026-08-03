# ResilientWorkerKit.HealthChecks

Health-check integration for
[ResilientWorkerKit](https://www.nuget.org/packages/ResilientWorkerKit) jobs: per-job
Healthy / Degraded / Unhealthy evaluation with configurable thresholds and stuck-job
detection, plugged into the standard `Microsoft.Extensions.Diagnostics.HealthChecks`
pipeline.

## Quick start

```csharp
builder.Services.AddHealthChecks().AddResilientWorkerKit(tags: ["ready"]);
```

```csharp
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});
```

The check aggregates every registered job: consecutive-failure thresholds decide
Degraded/Unhealthy, a job whose execution has been running past its expected bound is
reported as stuck, and the payload names the offending jobs so an operator does not have to
guess. Thresholds are configured per job at registration.

## Links

[Repository](https://github.com/mFurkanHiz/ResilientWorkerKit) ·
[Health checks guide](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/docs/health-checks.md) ·
[Changelog](https://github.com/mFurkanHiz/ResilientWorkerKit/blob/main/CHANGELOG.md) ·
MIT licensed · `net10.0` and `net8.0`

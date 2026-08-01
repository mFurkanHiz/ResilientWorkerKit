using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ResilientWorkerKit.HealthChecks;

/// <summary>
/// Aggregate health check over all registered jobs. Each job is evaluated in this order:
/// <list type="number">
/// <item>A job that has never run is Healthy — deploying a new job must not page anyone.</item>
/// <item>Running longer than the stuck threshold (explicit
/// <see cref="JobHealthThresholds.StuckAfter"/>, or 2× the job's total timeout) → Degraded
/// ("possibly stuck").</item>
/// <item>Consecutive failures ≥ <see cref="JobHealthThresholds.UnhealthyAfterConsecutiveFailures"/> → Unhealthy.</item>
/// <item>No successful execution for longer than
/// <see cref="JobHealthThresholds.UnhealthyWhenNoSuccessFor"/> → Unhealthy. A job that has run but
/// never succeeded is measured from its first observed start, so "never succeeded" also trips this rule.</item>
/// <item>Consecutive failures ≥ <see cref="JobHealthThresholds.DegradedAfterConsecutiveFailures"/> → Degraded.</item>
/// <item>Otherwise Healthy.</item>
/// </list>
/// The aggregate status is the worst individual status; details for every job are exposed in
/// the health entry's data dictionary. Health state is in-memory and resets on restart.
/// </summary>
public sealed class WorkerKitHealthCheck : IHealthCheck
{
    private readonly IJobHealthTracker _tracker;
    private readonly IJobRegistry _registry;
    private readonly TimeProvider _time;

    /// <summary>Creates the health check.</summary>
    public WorkerKitHealthCheck(IJobHealthTracker tracker, IJobRegistry registry, TimeProvider time)
    {
        _tracker = tracker;
        _registry = registry;
        _time = time;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var worst = HealthStatus.Healthy;
        var problems = new List<string>();

        foreach (var definition in _registry.Jobs)
        {
            if (!definition.Enabled)
            {
                data[$"{definition.JobId}"] = "disabled";
                continue;
            }

            var snapshot = _tracker.Get(definition.JobId);
            var (status, detail) = Evaluate(definition, snapshot, now);
            data[definition.JobId] = detail;

            if (status < worst)
            {
                // HealthStatus: Unhealthy = 0, Degraded = 1, Healthy = 2 — lower is worse.
                worst = status;
            }

            if (status != HealthStatus.Healthy)
            {
                problems.Add($"{definition.JobId}: {detail}");
            }
        }

        var description = problems.Count == 0
            ? $"{_registry.Jobs.Count(j => j.Enabled)} job(s) healthy"
            : string.Join("; ", problems);

        return Task.FromResult(new HealthCheckResult(worst, description, data: data));
    }

    private static (HealthStatus Status, string Detail) Evaluate(JobDefinition definition, JobHealthSnapshot? snapshot, DateTimeOffset now)
    {
        var thresholds = definition.HealthThresholds;

        if (snapshot is null || snapshot.LastStartedAtUtc is null)
        {
            var next = snapshot?.NextOccurrenceUtc;
            return (HealthStatus.Healthy, next is { } n
                ? $"not yet run; next occurrence {Format(n)}"
                : "not yet run");
        }

        // Stuck detection.
        var stuckAfter = thresholds.StuckAfter
            ?? (definition.Timeout is { } timeout ? timeout * 2 : null);
        if (snapshot.IsRunning && snapshot.RunningSinceUtc is { } since && stuckAfter is { } limit && now - since > limit)
        {
            return (HealthStatus.Degraded, $"possibly stuck: running since {Format(since)} (> {limit})");
        }

        if (snapshot.ConsecutiveFailures >= thresholds.UnhealthyAfterConsecutiveFailures)
        {
            return (HealthStatus.Unhealthy, $"{snapshot.ConsecutiveFailures} consecutive failures (last: {snapshot.LastResult})");
        }

        if (thresholds.UnhealthyWhenNoSuccessFor is { } noSuccessWindow && snapshot.ConsecutiveFailures > 0)
        {
            // A job that has run but never succeeded is the worst case, so it must be able to
            // trip this rule too: fall back to the first observed start as the baseline.
            var baseline = snapshot.LastSuccessAtUtc ?? snapshot.FirstStartedAtUtc;
            if (baseline is { } baselineUtc && now - baselineUtc > noSuccessWindow)
            {
                return (HealthStatus.Unhealthy, snapshot.LastSuccessAtUtc is null
                    ? $"no successful execution ever; first attempt {Format(baselineUtc)}"
                    : $"no successful execution since {Format(baselineUtc)}");
            }
        }

        if (snapshot.ConsecutiveFailures >= thresholds.DegradedAfterConsecutiveFailures)
        {
            return (HealthStatus.Degraded, $"{snapshot.ConsecutiveFailures} consecutive failures (last: {snapshot.LastResult})");
        }

        var parts = new List<string>(4);
        parts.Add(snapshot.IsRunning ? "running" : $"last result {snapshot.LastResult}");
        if (snapshot.LastSuccessAtUtc is { } success)
        {
            parts.Add($"last success {Format(success)}");
        }

        if (snapshot.NextOccurrenceUtc is { } upcoming)
        {
            parts.Add($"next {Format(upcoming)}");
        }

        return (HealthStatus.Healthy, string.Join(", ", parts));
    }

    private static string Format(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

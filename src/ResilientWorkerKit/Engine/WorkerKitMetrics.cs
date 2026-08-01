using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ResilientWorkerKit.Engine;

/// <summary>
/// Metrics and tracing sources. Consumable by OpenTelemetry by subscribing to the meter
/// <c>ResilientWorkerKit</c> and the activity source <c>ResilientWorkerKit</c> — no adapter
/// package required. Tags are limited to low-cardinality values (job id, status, policy);
/// execution ids never become tags.
/// </summary>
public sealed class WorkerKitMetrics : IDisposable
{
    /// <summary>Meter and activity source name.</summary>
    public const string MeterName = "ResilientWorkerKit";

    /// <summary>The activity source used for per-execution tracing.</summary>
    public static ActivitySource ActivitySource { get; } = new(MeterName);

    private readonly Meter _meter;
    private readonly Counter<long> _executions;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _misfires;
    private readonly Counter<long> _overlapsSkipped;
    private readonly Counter<long> _deadLetters;
    private readonly Histogram<double> _duration;
    private readonly UpDownCounter<long> _running;

    /// <summary>Creates the metrics container (register as a singleton).</summary>
    public WorkerKitMetrics() : this(null)
    {
    }

    /// <summary>Creates the metrics container using a custom meter factory (tests).</summary>
    public WorkerKitMetrics(IMeterFactory? meterFactory)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _executions = _meter.CreateCounter<long>("workerkit.job.executions", unit: "{execution}",
            description: "Finished job executions by status.");
        _retries = _meter.CreateCounter<long>("workerkit.job.retries", unit: "{retry}",
            description: "Retry attempts scheduled after transient failures.");
        _misfires = _meter.CreateCounter<long>("workerkit.job.misfires", unit: "{occurrence}",
            description: "Missed schedule occurrences detected.");
        _overlapsSkipped = _meter.CreateCounter<long>("workerkit.job.overlap_skipped", unit: "{occurrence}",
            description: "Occurrences skipped or queued because the previous execution was still running.");
        _deadLetters = _meter.CreateCounter<long>("workerkit.job.dead_letters", unit: "{record}",
            description: "Dead-letter records created.");
        _duration = _meter.CreateHistogram<double>("workerkit.job.duration", unit: "s",
            description: "Job execution duration in seconds.");
        _running = _meter.CreateUpDownCounter<long>("workerkit.job.running", unit: "{execution}",
            description: "Currently running job executions.");
    }

    internal void ExecutionStarted(string jobId)
        => _running.Add(1, new KeyValuePair<string, object?>("job.id", jobId));

    internal void ExecutionFinished(string jobId, JobExecutionStatus status, double durationSeconds)
    {
        var jobTag = new KeyValuePair<string, object?>("job.id", jobId);
        var statusTag = new KeyValuePair<string, object?>("status", status.ToString());
        _running.Add(-1, jobTag);
        _executions.Add(1, jobTag, statusTag);
        _duration.Record(durationSeconds, jobTag, statusTag);
    }

    internal void RetryScheduled(string jobId)
        => _retries.Add(1, new KeyValuePair<string, object?>("job.id", jobId));

    internal void MisfireDetected(string jobId, MisfirePolicy policy)
        => _misfires.Add(1,
            new KeyValuePair<string, object?>("job.id", jobId),
            new KeyValuePair<string, object?>("policy", policy.ToString()));

    internal void OverlapSkipped(string jobId)
        => _overlapsSkipped.Add(1, new KeyValuePair<string, object?>("job.id", jobId));

    internal void DeadLetterCreated(string jobId)
        => _deadLetters.Add(1, new KeyValuePair<string, object?>("job.id", jobId));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}

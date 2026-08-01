using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResilientWorkerKit.Engine;
using ResilientWorkerKit.Stores;

namespace ResilientWorkerKit.UnitTests.TestInfrastructure;

/// <summary>A job whose body is supplied by the test.</summary>
internal sealed class DelegateJob : IWorkerJob
{
    private readonly Func<JobExecutionContext, CancellationToken, Task> _body;

    public DelegateJob(Func<JobExecutionContext, CancellationToken, Task> body) => _body = body;

    public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        => _body(context, cancellationToken);
}

/// <summary>Everything needed to exercise <see cref="JobRunner"/> against in-memory stores.</summary>
internal sealed class RunnerHarness : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly WorkerKitMetrics _metrics;

    public RunnerHarness(Func<JobExecutionContext, CancellationToken, Task> body, TimeProvider? time = null, IJobExecutionStore? executionStore = null)
    {
        Time = time ?? TimeProvider.System;
        Checkpoints = new InMemoryJobCheckpointStore();
        Executions = new InMemoryJobExecutionStore();
        ExecutionStoreInUse = executionStore ?? Executions;
        Idempotency = new InMemoryIdempotencyStore(Time);
        DeadLetters = new InMemoryDeadLetterStore();
        Health = new JobHealthTracker();
        _metrics = new WorkerKitMetrics();

        var services = new ServiceCollection();
        services.AddScoped(_ => new DelegateJob(body));
        _serviceProvider = services.BuildServiceProvider();

        Runner = new JobRunner(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            ExecutionStoreInUse,
            Checkpoints,
            Idempotency,
            DeadLetters,
            new InProcessJobLockProvider(),
            new DefaultJobFailureClassifier(),
            Health,
            _metrics,
            new WorkerKitOptions(),
            Time);
    }

    public TimeProvider Time { get; }

    public InMemoryJobCheckpointStore Checkpoints { get; }

    public InMemoryJobExecutionStore Executions { get; }

    public IJobExecutionStore ExecutionStoreInUse { get; }

    public InMemoryIdempotencyStore Idempotency { get; }

    public InMemoryDeadLetterStore DeadLetters { get; }

    public JobHealthTracker Health { get; }

    public JobRunner Runner { get; }

    /// <summary>Builds a validated job definition. Retry delays default to zero for fast tests.</summary>
    public static JobDefinition Definition(Action<JobBuilder<DelegateJob>>? configure = null, string jobId = "test-job")
    {
        var builder = new JobBuilder<DelegateJob>(jobId);
        builder.WithRetry(r =>
        {
            r.BaseDelay = TimeSpan.Zero;
            r.MaxDelay = TimeSpan.Zero;
            r.JitterFactor = 0;
        });
        configure?.Invoke(builder);
        return builder.Build();
    }

    public JobScheduleOccurrence Occurrence()
    {
        var now = Time.GetUtcNow();
        return new JobScheduleOccurrence(now, now.UtcDateTime, "test:" + Guid.NewGuid().ToString("n"));
    }

    public Task<JobRunResult?> RunAsync(JobDefinition definition, CancellationToken stoppingToken = default)
        => Runner.RunAsync(definition, Occurrence(), "schedule", presetExecutionId: null, NullLogger.Instance, stoppingToken);

    public async ValueTask DisposeAsync()
    {
        _metrics.Dispose();
        await _serviceProvider.DisposeAsync();
    }
}

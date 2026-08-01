using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ResilientWorkerKit.Engine;
using ResilientWorkerKit.Stores;

namespace ResilientWorkerKit.UnitTests.TestInfrastructure;

/// <summary>
/// Drives a real <see cref="JobScheduleLoop"/> deterministically with a FakeTimeProvider.
/// Every schedule delay is virtual; only tiny real yields let continuations run.
/// </summary>
internal sealed class LoopHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-01T10:00:00Z");

    private readonly ServiceProvider _serviceProvider;
    private readonly WorkerKitMetrics _metrics;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loopTasks = new();

    public LoopHarness(Func<JobExecutionContext, CancellationToken, Task> body)
    {
        Time = new FakeTimeProvider(T0);
        Executions = new InMemoryJobExecutionStore();
        Checkpoints = new InMemoryJobCheckpointStore();
        Idempotency = new InMemoryIdempotencyStore(Time);
        DeadLetters = new InMemoryDeadLetterStore();
        PendingOccurrences = new InMemoryPendingOccurrenceStore();
        Health = new JobHealthTracker();
        _metrics = new WorkerKitMetrics();

        var services = new ServiceCollection();
        services.AddScoped(_ => new DelegateJob(body));
        _serviceProvider = services.BuildServiceProvider();

        Runner = new JobRunner(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Executions, Checkpoints, Idempotency, DeadLetters,
            new InProcessJobLockProvider(),
            new DefaultJobFailureClassifier(),
            Health, _metrics, new WorkerKitOptions(), Time);
    }

    public FakeTimeProvider Time { get; }

    public InMemoryJobExecutionStore Executions { get; }

    public InMemoryJobCheckpointStore Checkpoints { get; }

    public InMemoryIdempotencyStore Idempotency { get; }

    public InMemoryDeadLetterStore DeadLetters { get; }

    public InMemoryPendingOccurrenceStore PendingOccurrences { get; }

    public JobHealthTracker Health { get; }

    public JobRunner Runner { get; }

    public JobScheduleLoop StartLoop(JobDefinition definition)
    {
        var loop = new JobScheduleLoop(
            definition, Runner, Executions, PendingOccurrences, Health, _metrics, Time, NullLogger.Instance);
        _loopTasks.Add(loop.RunAsync(_cts.Token));
        return loop;
    }

    /// <summary>Seeds a finished execution into history (for anchor recovery / misfire tests).</summary>
    public Task SeedExecutionAsync(
        string jobId,
        DateTimeOffset scheduledAtUtc,
        JobExecutionStatus status = JobExecutionStatus.Completed,
        string triggerType = "schedule",
        string? identityToken = null)
    {
        var token = identityToken
            ?? scheduledAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var record = new JobExecutionRecord
        {
            JobId = jobId,
            ExecutionId = Guid.NewGuid().ToString("n"),
            ScheduledExecutionId = $"{jobId}:{token}",
            ScheduledAtUtc = scheduledAtUtc,
            StartedAtUtc = scheduledAtUtc,
            CompletedAtUtc = scheduledAtUtc.AddSeconds(1),
            CreatedAtUtc = scheduledAtUtc,
            Status = status,
            TriggerType = triggerType,
        };
        return Executions.CreateAsync(record);
    }

    /// <summary>Waits (real time, bounded) for a condition without advancing virtual time.</summary>
    public async Task WaitUntilAsync(Func<Task<bool>> condition, int maxIterations = 300)
    {
        for (var i = 0; i < maxIterations; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met without advancing time.");
    }

    /// <summary>Asserts a condition stays false while real time passes (no virtual advance).</summary>
    public async Task AssertNotHappeningAsync(Func<Task<bool>> condition, int iterations = 20)
    {
        for (var i = 0; i < iterations; i++)
        {
            Assert.False(await condition(), "Condition happened although it should not have.");
            await Task.Delay(10);
        }
    }

    /// <summary>Advances virtual time in steps until the condition holds.</summary>
    public async Task AdvanceUntilAsync(TimeSpan step, Func<Task<bool>> condition, int maxSteps = 300)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (await condition())
            {
                return;
            }

            Time.Advance(step);
            await Task.Delay(10);
        }

        throw new TimeoutException($"Condition was not met after {maxSteps} × {step} of virtual time.");
    }

    public async Task<IReadOnlyList<JobExecutionRecord>> RecordsAsync(string jobId)
        => await Executions.GetRecentAsync(jobId, 100);

    public async Task<int> CountAsync(string jobId, string? triggerType = null)
        => (await RecordsAsync(jobId)).Count(r => triggerType is null || r.TriggerType == triggerType);

    public async Task StopAsync()
    {
        _cts.Cancel();
        foreach (var task in _loopTasks)
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        _loopTasks.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            await StopAsync();
        }

        _metrics.Dispose();
        _cts.Dispose();
        await _serviceProvider.DisposeAsync();
    }
}

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

    private readonly IJobExecutionStore _executionStoreForLoop;
    private readonly IPendingOccurrenceStore _pendingStoreForLoop;

    /// <param name="body">The job body.</param>
    /// <param name="yieldingStores">
    /// Wrap the stores so every call yields before completing. Leave this on for anything that
    /// exercises the loop's reaction to state written by a running execution: with synchronous
    /// stores an execution can finish inside the call that started it, which hides whether the
    /// loop would ever have noticed on its own.
    /// </param>
    /// <param name="wrapPendingStore">
    /// Optional extra decorator around the pending store the loop sees — for fault-injection
    /// tests (for example, a store whose AddAsync fails on demand).
    /// </param>
    /// <param name="lockProvider">
    /// Optional job-lock provider; defaults to the in-process one. Tests use a denying stub to
    /// reach the runner-declined path, which a single in-process loop cannot produce naturally.
    /// </param>
    public LoopHarness(
        Func<JobExecutionContext, CancellationToken, Task> body,
        bool yieldingStores = false,
        Func<IPendingOccurrenceStore, IPendingOccurrenceStore>? wrapPendingStore = null,
        IJobLockProvider? lockProvider = null)
    {
        Time = new FakeTimeProvider(T0);
        Executions = new InMemoryJobExecutionStore();
        Checkpoints = new InMemoryJobCheckpointStore();
        Idempotency = new InMemoryIdempotencyStore(Time);
        DeadLetters = new InMemoryDeadLetterStore();
        PendingOccurrences = new InMemoryPendingOccurrenceStore();
        Health = new JobHealthTracker();
        _metrics = new WorkerKitMetrics();

        _executionStoreForLoop = yieldingStores ? new YieldingJobExecutionStore(Executions) : Executions;
        _pendingStoreForLoop = yieldingStores ? new YieldingPendingOccurrenceStore(PendingOccurrences) : PendingOccurrences;
        if (wrapPendingStore is not null)
        {
            _pendingStoreForLoop = wrapPendingStore(_pendingStoreForLoop);
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new DelegateJob(body));
        _serviceProvider = services.BuildServiceProvider();

        Runner = new JobRunner(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _executionStoreForLoop, Checkpoints, Idempotency, DeadLetters,
            lockProvider ?? new InProcessJobLockProvider(),
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

    /// <summary>Options handed to the loop; adjust before StartLoop when a test needs to.</summary>
    public WorkerKitOptions Options { get; } = new();

    public JobScheduleLoop StartLoop(JobDefinition definition)
    {
        var loop = new JobScheduleLoop(
            definition, Runner, _executionStoreForLoop, _pendingStoreForLoop,
            Health, _metrics, Options, Time, NullLogger.Instance);
        _loopTasks.Add(loop.RunAsync(_cts.Token));
        return loop;
    }

    /// <summary>Seeds a finished execution into history (for anchor recovery / misfire tests).</summary>
    public Task SeedExecutionAsync(
        string jobId,
        DateTimeOffset scheduledAtUtc,
        JobExecutionStatus status = JobExecutionStatus.Completed,
        string triggerType = "schedule",
        string? identityToken = null,
        JobFailureKind? failureKind = null)
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
            FailureKind = failureKind,
        };
        return Executions.CreateAsync(record);
    }

    /// <summary>Queues a pending occurrence, optionally pre-leased (e.g. by a "dead" host).</summary>
    public async Task<PendingOccurrence> SeedPendingAsync(
        string jobId,
        string identityToken,
        DateTimeOffset dueAtUtc,
        int followUpOrdinal = 1,
        string? leaseOwner = null,
        DateTimeOffset? leaseAcquiredAtUtc = null,
        TimeSpan? leaseDuration = null)
    {
        var row = new PendingOccurrence
        {
            Id = Guid.NewGuid().ToString("n"),
            JobId = jobId,
            DueAtUtc = dueAtUtc,
            IdentityToken = identityToken,
            OriginScheduledExecutionId = $"{jobId}:{identityToken.Split('+')[0]}",
            FollowUpOrdinal = followUpOrdinal,
            CreatedAtUtc = dueAtUtc.AddMinutes(-5),
        };
        Assert.True(await PendingOccurrences.AddAsync(row));

        if (leaseOwner is not null)
        {
            var token = await PendingOccurrences.TryAcquireLeaseAsync(
                row.Id, leaseOwner,
                leaseDuration ?? TimeSpan.FromMinutes(5),
                leaseAcquiredAtUtc ?? dueAtUtc);
            Assert.NotNull(token);
        }

        return row;
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

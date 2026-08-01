using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ResilientWorkerKit.Engine;

/// <summary>
/// The single hosted service owning all job schedule loops. Responsible for startup recovery,
/// spawning one isolated loop per enabled job, and graceful shutdown draining.
/// </summary>
internal sealed class WorkerKitHostedService : BackgroundService
{
    private readonly IJobRegistry _registry;
    private readonly JobRunner _runner;
    private readonly IJobExecutionStore _executionStore;
    private readonly JobHealthTracker _health;
    private readonly WorkerKitMetrics _metrics;
    private readonly WorkerKitOptions _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, JobScheduleLoop> _loops = new(StringComparer.Ordinal);

    public WorkerKitHostedService(
        IJobRegistry registry,
        JobRunner runner,
        IJobExecutionStore executionStore,
        JobHealthTracker health,
        WorkerKitMetrics metrics,
        WorkerKitOptions options,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _runner = runner;
        _executionStore = executionStore;
        _health = health;
        _metrics = metrics;
        _options = options;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("ResilientWorkerKit.Host");

        foreach (var definition in _registry.Jobs)
        {
            _health.RegisterJob(definition);
            if (definition.Enabled)
            {
                var jobLogger = _loggerFactory.CreateLogger($"ResilientWorkerKit.Jobs.{definition.JobId}");
                _loops[definition.JobId] = new JobScheduleLoop(
                    definition, _runner, _executionStore, _health, _metrics, _time, jobLogger);
            }
        }
    }

    /// <summary>Routes a manual trigger request to the job's loop.</summary>
    internal bool TryEnqueueManualTrigger(string jobId, ManualTriggerRequest request)
    {
        if (!_loops.TryGetValue(jobId, out var loop))
        {
            return false;
        }

        loop.EnqueueManualTrigger(request);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RunStartupRecovery)
        {
            try
            {
                var recovered = await _executionStore.MarkRunningAsAbandonedAsync(stoppingToken).ConfigureAwait(false);
                if (recovered > 0)
                {
                    JobLog.AbandonedExecutionsRecovered(_logger, recovered);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                JobLog.StoreOperationFailed(_logger, ex, "StartupRecovery");
            }
        }

        var loopTasks = _loops.Values
            .Select(loop => Task.Run(() => loop.RunAsync(stoppingToken), CancellationToken.None))
            .ToList();

        if (loopTasks.Count == 0)
        {
            return;
        }

        // Wait for shutdown; loops never throw (last-resort catch inside RunAsync).
        await Task.WhenAll(loopTasks).ConfigureAwait(false);

        // Graceful shutdown: loops have stopped starting new occurrences; wait for running
        // executions to observe cancellation and finish within the grace period.
        var runningTasks = _loops.Values.SelectMany(l => l.GetRunningTasks()).ToList();
        JobLog.ShutdownStarted(_logger, _options.ShutdownGracePeriod, runningTasks.Count);

        var allFinished = true;
        if (runningTasks.Count > 0)
        {
            var drain = Task.WhenAll(runningTasks);
            var timeout = Task.Delay(_options.ShutdownGracePeriod, _time, CancellationToken.None);
            var first = await Task.WhenAny(drain, timeout).ConfigureAwait(false);
            allFinished = first == drain;
        }

        JobLog.ShutdownCompleted(_logger, allFinished);
    }
}

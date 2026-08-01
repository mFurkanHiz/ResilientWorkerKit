namespace ResilientWorkerKit.Engine;

/// <summary>
/// In-memory per-job health state, updated by the engine and read by the health check
/// package. Thread-safe; snapshots are immutable copies.
/// </summary>
public sealed class JobHealthTracker : IJobHealthTracker, IJobProgressReporter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    private sealed class State
    {
        public bool Enabled = true;
        public int RunningCount;
        public DateTimeOffset? RunningSinceUtc;
        public DateTimeOffset? LastScheduledAtUtc;
        public DateTimeOffset? LastStartedAtUtc;
        public DateTimeOffset? LastCompletedAtUtc;
        public DateTimeOffset? LastSuccessAtUtc;
        public DateTimeOffset? LastFailureAtUtc;
        public JobExecutionStatus? LastResult;
        public double? LastDurationMs;
        public int ConsecutiveFailures;
        public DateTimeOffset? NextOccurrenceUtc;
        public string? LastProgress;
        public string? LastCheckpointSummary;
    }

    internal void RegisterJob(JobDefinition definition)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(definition.JobId, out var state))
            {
                _states[definition.JobId] = state = new State();
            }

            state.Enabled = definition.Enabled;
        }
    }

    internal void OnNextOccurrence(string jobId, DateTimeOffset? nextUtc)
    {
        lock (_gate)
        {
            GetState(jobId).NextOccurrenceUtc = nextUtc;
        }
    }

    internal void OnExecutionStarted(string jobId, DateTimeOffset startedAtUtc, DateTimeOffset scheduledAtUtc)
    {
        lock (_gate)
        {
            var state = GetState(jobId);
            state.RunningCount++;
            state.RunningSinceUtc ??= startedAtUtc;
            state.LastStartedAtUtc = startedAtUtc;
            state.LastScheduledAtUtc = scheduledAtUtc;
            state.LastProgress = null;
        }
    }

    internal void OnExecutionFinished(string jobId, JobExecutionStatus status, DateTimeOffset completedAtUtc, double durationMs)
    {
        lock (_gate)
        {
            var state = GetState(jobId);
            state.RunningCount = Math.Max(0, state.RunningCount - 1);
            if (state.RunningCount == 0)
            {
                state.RunningSinceUtc = null;
            }

            state.LastCompletedAtUtc = completedAtUtc;
            state.LastResult = status;
            state.LastDurationMs = durationMs;

            // Cancelled and Abandoned count as neither success nor failure: a shutdown or a
            // crashed process must not drive the job towards Unhealthy on its own.
            switch (status)
            {
                case JobExecutionStatus.Completed:
                    state.LastSuccessAtUtc = completedAtUtc;
                    state.ConsecutiveFailures = 0;
                    break;
                case JobExecutionStatus.Failed:
                case JobExecutionStatus.TimedOut:
                    state.LastFailureAtUtc = completedAtUtc;
                    state.ConsecutiveFailures++;
                    break;
            }
        }
    }

    internal void OnCheckpointSaved(string jobId, string summary)
    {
        lock (_gate)
        {
            GetState(jobId).LastCheckpointSummary = summary;
        }
    }

    /// <inheritdoc />
    public void Report(string jobId, string executionId, string message)
    {
        lock (_gate)
        {
            GetState(jobId).LastProgress = message;
        }
    }

    /// <inheritdoc />
    public JobHealthSnapshot? Get(string jobId)
    {
        lock (_gate)
        {
            return _states.TryGetValue(jobId, out var state) ? ToSnapshot(jobId, state) : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JobHealthSnapshot> GetAll()
    {
        lock (_gate)
        {
            return _states.Select(kv => ToSnapshot(kv.Key, kv.Value)).ToList();
        }
    }

    private State GetState(string jobId)
    {
        if (!_states.TryGetValue(jobId, out var state))
        {
            _states[jobId] = state = new State();
        }

        return state;
    }

    private static JobHealthSnapshot ToSnapshot(string jobId, State s) => new()
    {
        JobId = jobId,
        Enabled = s.Enabled,
        IsRunning = s.RunningCount > 0,
        RunningSinceUtc = s.RunningSinceUtc,
        LastScheduledAtUtc = s.LastScheduledAtUtc,
        LastStartedAtUtc = s.LastStartedAtUtc,
        LastCompletedAtUtc = s.LastCompletedAtUtc,
        LastSuccessAtUtc = s.LastSuccessAtUtc,
        LastFailureAtUtc = s.LastFailureAtUtc,
        LastResult = s.LastResult,
        LastDurationMs = s.LastDurationMs,
        ConsecutiveFailures = s.ConsecutiveFailures,
        NextOccurrenceUtc = s.NextOccurrenceUtc,
        LastProgress = s.LastProgress,
        LastCheckpointSummary = s.LastCheckpointSummary,
    };
}

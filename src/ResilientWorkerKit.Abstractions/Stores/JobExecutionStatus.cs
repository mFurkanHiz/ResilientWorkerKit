namespace ResilientWorkerKit;

/// <summary>Terminal and non-terminal states of a job execution.</summary>
public enum JobExecutionStatus
{
    /// <summary>The execution is currently in progress.</summary>
    Running = 0,

    /// <summary>The job body returned normally.</summary>
    Completed = 1,

    /// <summary>The execution failed permanently or exhausted its retries.</summary>
    Failed = 2,

    /// <summary>The execution observed cooperative cancellation (host shutdown / manual stop).</summary>
    Cancelled = 3,

    /// <summary>The total execution timeout elapsed.</summary>
    TimedOut = 4,

    /// <summary>The record was still <see cref="Running"/> when a new host instance started; the owning process died.</summary>
    Abandoned = 5,
}

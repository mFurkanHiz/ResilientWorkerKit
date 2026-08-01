namespace ResilientWorkerKit;

/// <summary>What to do when a new occurrence fires while the previous execution is still running.</summary>
public enum OverlapPolicy
{
    /// <summary>
    /// Skip the new occurrence entirely. The safe default: no queue can build up, and the
    /// next regular occurrence runs normally.
    /// </summary>
    SkipNewExecution = 0,

    /// <summary>
    /// Remember at most one pending occurrence and run it immediately after the current
    /// execution finishes. Additional occurrences that fire while one is already queued are skipped.
    /// </summary>
    QueueSingleExecution = 1,

    /// <summary>Allow the new execution to run concurrently with the previous one.</summary>
    AllowConcurrentExecutions = 2,
}

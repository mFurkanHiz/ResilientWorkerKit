namespace ResilientWorkerKit;

/// <summary>
/// A background job executed by ResilientWorkerKit. Implementations contain only business
/// logic; scheduling, retry, timeouts, checkpoint plumbing, idempotency and failure isolation
/// are provided by the engine.
/// </summary>
public interface IWorkerJob
{
    /// <summary>
    /// Executes one occurrence of the job.
    /// </summary>
    /// <param name="context">
    /// Execution context: identity, scoped services, logger, checkpoint and idempotency access.
    /// </param>
    /// <param name="cancellationToken">
    /// Signalled on host shutdown, manual cancellation or when the execution timeout elapses.
    /// Implementations must propagate it into every I/O call they make.
    /// </param>
    Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

namespace ResilientWorkerKit;

/// <summary>
/// Retry policy for transient failures of one execution. Attempts share a single
/// <c>ExecutionId</c>; only <see cref="JobFailureKind.Transient"/> failures are retried.
/// </summary>
public sealed class JobRetryOptions
{
    /// <summary>Maximum number of retries after the first attempt (3 ⇒ up to 4 attempts). Default 3.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay of the exponential backoff. Default 2 seconds.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ceiling for the computed backoff delay, applied after jitter. Default 1 minute.
    /// A server-provided <c>Retry-After</c> hint is honored as given and is not capped by this.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Backoff multiplier per attempt (delay = BaseDelay × Multiplier^(retry-1)). Default 2.0.</summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Proportional jitter applied to each delay: the delay is multiplied by a random factor in
    /// [1 − JitterFactor, 1 + JitterFactor]. Default 0.2. Set 0 to disable.
    /// </summary>
    public double JitterFactor { get; set; } = 0.2;

    /// <summary>
    /// Optional timeout for a single attempt. An attempt that exceeds it is cancelled and
    /// classified as transient (retried). The total execution timeout is configured separately
    /// on the job (<c>WithTimeout</c>).
    /// </summary>
    public TimeSpan? AttemptTimeout { get; set; }
}

namespace ResilientWorkerKit;

/// <summary>
/// Durable retry policy applied <b>after</b> an execution has failed for good — the attempts
/// inside one execution (<see cref="JobRetryOptions"/>) having already been exhausted, or the
/// failure having been permanent.
/// <para>
/// The difference matters. <see cref="JobRetryOptions"/> retries in memory, on a scale of
/// seconds, keeping one <c>ExecutionId</c>; a restart during that window loses the retry.
/// A follow-up retry is queued durably, runs as a new execution linked to the original
/// occurrence, and survives a restart. Use it for planned actions that must eventually happen.
/// </para>
/// </summary>
public sealed class FollowUpRetryOptions
{
    /// <summary>How many follow-up executions to queue after the original failure. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay before the first follow-up. Default 5 minutes.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Multiplier applied per follow-up: with a 5-minute delay and a multiplier of 2 the
    /// follow-ups land at +5, +10 and +20 minutes. Default 1 (evenly spaced).
    /// </summary>
    public double BackoffMultiplier { get; set; } = 1.0;

    /// <summary>Ceiling for a computed follow-up delay. Default 6 hours.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Also queue a follow-up when the failure was classified
    /// <see cref="JobFailureKind.Permanent"/> or <see cref="JobFailureKind.Misconfigured"/>.
    /// Default false: a deterministic failure normally repeats, and retrying it wastes the
    /// window. Set true when "must eventually happen" outweighs that — for example when the
    /// permanent failure is expected to be fixed by an operator before the next attempt.
    /// </summary>
    public bool RetryPermanentFailures { get; set; }

    /// <summary>Computes the delay before the given 1-based follow-up.</summary>
    public TimeSpan DelayFor(int followUpOrdinal)
    {
        var exponent = Math.Max(0, followUpOrdinal - 1);
        var ms = Delay.TotalMilliseconds * Math.Pow(BackoffMultiplier, exponent);
        if (double.IsNaN(ms) || double.IsInfinity(ms))
        {
            return MaxDelay;
        }

        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, MaxDelay.TotalMilliseconds));
    }
}

namespace ResilientWorkerKit.Engine;

/// <summary>Computes retry delays: exponential backoff, proportional jitter, Retry-After hints.</summary>
internal static class RetryDelayCalculator
{
    /// <summary>
    /// Computes the delay before the given retry.
    /// </summary>
    /// <param name="options">The retry policy.</param>
    /// <param name="retryNumber">1-based retry number (1 = first retry).</param>
    /// <param name="retryAfterHint">
    /// Server-provided delay (e.g. an HTTP <c>Retry-After</c> header). When present it replaces
    /// the computed backoff entirely — the server's instruction wins in both directions, and
    /// <see cref="JobRetryOptions.MaxDelay"/> does not cap it.
    /// </param>
    /// <param name="jitterSample">A uniform random sample in [0,1); injectable for tests.</param>
    public static TimeSpan Compute(JobRetryOptions options, int retryNumber, TimeSpan? retryAfterHint, double jitterSample)
    {
        if (retryAfterHint is { } hint)
        {
            return hint < TimeSpan.Zero ? TimeSpan.Zero : hint;
        }

        var exponent = Math.Max(0, retryNumber - 1);
        var raw = options.BaseDelay.TotalMilliseconds * Math.Pow(options.BackoffMultiplier, exponent);

        if (options.JitterFactor > 0)
        {
            // Uniform in [1 − jitter, 1 + jitter].
            var factor = 1 - options.JitterFactor + (2 * options.JitterFactor * jitterSample);
            raw *= factor;
        }

        // Clamped last, so MaxDelay is a true ceiling rather than a pre-jitter cap.
        raw = Math.Clamp(raw, 0, options.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(raw);
    }
}

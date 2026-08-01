using ResilientWorkerKit.Engine;

namespace ResilientWorkerKit.UnitTests.Engine;

public class RetryDelayCalculatorTests
{
    private static JobRetryOptions Options(double jitter = 0) => new()
    {
        MaxRetries = 5,
        BaseDelay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromSeconds(30),
        BackoffMultiplier = 2.0,
        JitterFactor = jitter,
    };

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)] // capped by MaxDelay (would be 32)
    public void ExponentialBackoff_WithCap(int retryNumber, double expectedSeconds)
    {
        var delay = RetryDelayCalculator.Compute(Options(), retryNumber, retryAfterHint: null, jitterSample: 0.5);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void Jitter_StaysWithinConfiguredBounds()
    {
        var options = Options(jitter: 0.2);
        for (var sample = 0.0; sample <= 1.0; sample += 0.1)
        {
            var delay = RetryDelayCalculator.Compute(options, 1, null, sample);
            Assert.InRange(delay.TotalSeconds, 2 * 0.8, 2 * 1.2);
        }
    }

    [Fact]
    public void Jitter_ExtremeSamples_HitTheBounds()
    {
        var options = Options(jitter: 0.2);
        Assert.Equal(1.6, RetryDelayCalculator.Compute(options, 1, null, 0.0).TotalSeconds, precision: 6);
        Assert.Equal(2.4, RetryDelayCalculator.Compute(options, 1, null, 1.0).TotalSeconds, precision: 6);
    }

    [Fact]
    public void MaxDelay_IsATrueCeiling_EvenWithJitter()
    {
        var options = Options(jitter: 0.2);

        // Retry 5 would compute 32 s before the cap; the upward jitter must not push the
        // result past MaxDelay.
        var delay = RetryDelayCalculator.Compute(options, 5, null, jitterSample: 1.0);

        Assert.Equal(options.MaxDelay, delay);
        Assert.True(delay <= options.MaxDelay);
    }

    [Fact]
    public void RetryAfterHint_ReplacesComputedBackoff_InBothDirections()
    {
        // Longer than the backoff…
        Assert.Equal(TimeSpan.FromSeconds(45),
            RetryDelayCalculator.Compute(Options(), 1, TimeSpan.FromSeconds(45), 0.5));

        // …and shorter: the server's instruction wins either way.
        Assert.Equal(TimeSpan.FromMilliseconds(200),
            RetryDelayCalculator.Compute(Options(), 4, TimeSpan.FromMilliseconds(200), 0.5));
    }

    [Fact]
    public void RetryAfterHint_IsNotCappedByMaxDelay()
    {
        // A server asking for 5 minutes is honored even though MaxDelay is 30 s: the server
        // knows something the client does not.
        var delay = RetryDelayCalculator.Compute(Options(), 1, TimeSpan.FromMinutes(5), 0.5);
        Assert.Equal(TimeSpan.FromMinutes(5), delay);
    }

    [Fact]
    public void NegativeRetryAfterHint_ClampsToZero()
    {
        var delay = RetryDelayCalculator.Compute(Options(), 1, TimeSpan.FromSeconds(-5), 0.5);
        Assert.Equal(TimeSpan.Zero, delay);
    }
}

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
    public void RetryAfterHint_OverridesComputedBackoff()
    {
        var delay = RetryDelayCalculator.Compute(Options(), 1, TimeSpan.FromSeconds(45), 0.5);
        Assert.Equal(TimeSpan.FromSeconds(45), delay);
    }

    [Fact]
    public void NegativeRetryAfterHint_ClampsToZero()
    {
        var delay = RetryDelayCalculator.Compute(Options(), 1, TimeSpan.FromSeconds(-5), 0.5);
        Assert.Equal(TimeSpan.Zero, delay);
    }
}

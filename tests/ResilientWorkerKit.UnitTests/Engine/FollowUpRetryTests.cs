using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Engine;

public class FollowUpRetryOptionsTests
{
    [Fact]
    public void EvenlySpacedByDefault()
    {
        var options = new FollowUpRetryOptions { Delay = TimeSpan.FromMinutes(5) };

        Assert.Equal(TimeSpan.FromMinutes(5), options.DelayFor(1));
        Assert.Equal(TimeSpan.FromMinutes(5), options.DelayFor(2));
        Assert.Equal(TimeSpan.FromMinutes(5), options.DelayFor(3));
    }

    [Fact]
    public void BacksOffWhenAMultiplierIsSet()
    {
        var options = new FollowUpRetryOptions
        {
            Delay = TimeSpan.FromMinutes(5),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromHours(6),
        };

        Assert.Equal(TimeSpan.FromMinutes(5), options.DelayFor(1));
        Assert.Equal(TimeSpan.FromMinutes(10), options.DelayFor(2));
        Assert.Equal(TimeSpan.FromMinutes(20), options.DelayFor(3));
    }

    [Fact]
    public void ClampsToMaxDelay()
    {
        var options = new FollowUpRetryOptions
        {
            Delay = TimeSpan.FromMinutes(5),
            BackoffMultiplier = 10.0,
            MaxDelay = TimeSpan.FromHours(1),
        };

        Assert.Equal(TimeSpan.FromHours(1), options.DelayFor(5));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void StaysFiniteForExtremeOrdinals(int ordinal)
    {
        var options = new FollowUpRetryOptions
        {
            Delay = TimeSpan.FromMinutes(5),
            BackoffMultiplier = 3.0,
            MaxDelay = TimeSpan.FromHours(6),
        };

        Assert.InRange(options.DelayFor(ordinal), TimeSpan.Zero, options.MaxDelay);
    }
}

public class FollowUpRetryValidationTests
{
    [Fact]
    public void RejectsZeroAttempts()
        => Assert.Throws<JobConfigurationException>(() =>
            RunnerHarness.Definition(b => b.RetryLater(0, TimeSpan.FromMinutes(5))));

    [Fact]
    public void RejectsNonPositiveDelay()
        => Assert.Throws<JobConfigurationException>(() =>
            RunnerHarness.Definition(b => b.RetryLater(3, TimeSpan.Zero)));

    [Fact]
    public void RejectsMaxDelayBelowTheBaseDelay()
        => Assert.Throws<JobConfigurationException>(() => RunnerHarness.Definition(b => b.RetryLater(o =>
        {
            o.Delay = TimeSpan.FromMinutes(10);
            o.MaxDelay = TimeSpan.FromMinutes(1);
        })));

    [Fact]
    public void RejectsBackoffBelowOne()
        => Assert.Throws<JobConfigurationException>(() => RunnerHarness.Definition(b => b.RetryLater(o =>
        {
            o.BackoffMultiplier = 0.5;
        })));

    [Fact]
    public void IsOffUnlessConfigured()
        => Assert.Null(RunnerHarness.Definition().FollowUpRetry);

    [Fact]
    public void CarriesTheConfiguredPolicy()
    {
        var definition = RunnerHarness.Definition(b => b.RetryLater(4, TimeSpan.FromMinutes(7)));

        Assert.Equal(4, definition.FollowUpRetry!.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(7), definition.FollowUpRetry.Delay);
        Assert.False(definition.FollowUpRetry.RetryPermanentFailures);
    }
}

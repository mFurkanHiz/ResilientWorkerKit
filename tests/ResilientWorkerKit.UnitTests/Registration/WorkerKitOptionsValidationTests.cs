using ResilientWorkerKit.Registration;

namespace ResilientWorkerKit.UnitTests.Registration;

/// <summary>
/// Fail-fast validation of the global options the lease guarantees rest on. A bad value must
/// stop the host with a clear message — the alternative failure modes are duplicate
/// executions (an instantly-expired lease) or a first-insert column overflow at runtime.
/// </summary>
public class WorkerKitOptionsValidationTests
{
    [Fact]
    public void DefaultOptions_AreValid()
        => WorkerKitOptionsValidator.Validate(new WorkerKitOptions());

    [Fact]
    public void APositiveLeaseDuration_IsAccepted()
        => WorkerKitOptionsValidator.Validate(new WorkerKitOptions
        {
            PendingOccurrenceLeaseDuration = TimeSpan.FromSeconds(30),
        });

    [Fact]
    public void AZeroLeaseDuration_IsRejected()
    {
        // Zero means every lease is expired the instant it is acquired: a second host could
        // acquire the same occurrence immediately — duplicate execution by configuration.
        // The store-level proof of that hazard is in the lease contract suite.
        var ex = Assert.Throws<JobConfigurationException>(() =>
            WorkerKitOptionsValidator.Validate(new WorkerKitOptions
            {
                PendingOccurrenceLeaseDuration = TimeSpan.Zero,
            }));
        Assert.Contains("PendingOccurrenceLeaseDuration", ex.Message);
    }

    [Fact]
    public void ANegativeLeaseDuration_IsRejected()
        => Assert.Throws<JobConfigurationException>(() =>
            WorkerKitOptionsValidator.Validate(new WorkerKitOptions
            {
                PendingOccurrenceLeaseDuration = TimeSpan.FromSeconds(-1),
            }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingHostInstanceId_IsRejected(string hostInstanceId)
    {
        var ex = Assert.Throws<JobConfigurationException>(() =>
            WorkerKitOptionsValidator.Validate(new WorkerKitOptions
            {
                HostInstanceId = hostInstanceId,
            }));
        Assert.Contains("HostInstanceId", ex.Message);
    }

    [Fact]
    public void AHostInstanceIdAtTheColumnLimit_IsAccepted()
        => WorkerKitOptionsValidator.Validate(new WorkerKitOptions
        {
            HostInstanceId = new string('h', WorkerKitOptionsValidator.MaxHostInstanceIdLength),
        });

    [Fact]
    public void AHostInstanceIdPastTheColumnLimit_IsRejected()
    {
        // The persistence model stores the id in nvarchar(200) columns; failing here beats
        // failing on the first insert.
        var ex = Assert.Throws<JobConfigurationException>(() =>
            WorkerKitOptionsValidator.Validate(new WorkerKitOptions
            {
                HostInstanceId = new string('h', WorkerKitOptionsValidator.MaxHostInstanceIdLength + 1),
            }));
        Assert.Contains("200", ex.Message);
    }

    [Fact]
    public void ANegativeLockAcquireTimeout_IsRejected()
        => Assert.Throws<JobConfigurationException>(() =>
            WorkerKitOptionsValidator.Validate(new WorkerKitOptions
            {
                LockAcquireTimeout = TimeSpan.FromSeconds(-1),
            }));

    [Fact]
    public void AZeroLockAcquireTimeout_IsAccepted()
        => WorkerKitOptionsValidator.Validate(new WorkerKitOptions
        {
            LockAcquireTimeout = TimeSpan.Zero,
        });

    [Fact]
    public void ANegativeShutdownGracePeriod_IsRejected()
        => Assert.Throws<JobConfigurationException>(() =>
            WorkerKitOptionsValidator.Validate(new WorkerKitOptions
            {
                ShutdownGracePeriod = TimeSpan.FromSeconds(-1),
            }));

    [Fact]
    public void AZeroShutdownGracePeriod_IsAccepted()
        => WorkerKitOptionsValidator.Validate(new WorkerKitOptions
        {
            ShutdownGracePeriod = TimeSpan.Zero,
        });
}

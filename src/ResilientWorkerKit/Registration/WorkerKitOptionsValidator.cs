namespace ResilientWorkerKit.Registration;

/// <summary>
/// Fail-fast validation of <see cref="WorkerKitOptions"/>, run before any loop is constructed.
/// The lease guarantees rest on these values — a misconfiguration must stop the host with a
/// clear message, not surface as duplicate executions or a first-insert failure at 3 a.m.
/// </summary>
internal static class WorkerKitOptionsValidator
{
    /// <summary>
    /// The persistence model stores the host identity in nvarchar(200) columns
    /// (WorkerKitExecutions.HostInstanceId, WorkerKitPendingOccurrences.LeaseOwner). A longer
    /// id would fail on the first insert instead of at startup.
    /// </summary>
    internal const int MaxHostInstanceIdLength = 200;

    public static void Validate(WorkerKitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.HostInstanceId))
        {
            throw new JobConfigurationException(
                "WorkerKitOptions.HostInstanceId must not be null, empty or whitespace: it is " +
                "recorded on every execution and identifies the lease owner of every pending " +
                "occurrence.");
        }

        if (options.HostInstanceId.Length > MaxHostInstanceIdLength)
        {
            throw new JobConfigurationException(
                $"WorkerKitOptions.HostInstanceId is {options.HostInstanceId.Length} characters; " +
                $"the persistence model stores it in {MaxHostInstanceIdLength}-character columns, " +
                "so a longer id would fail on the first insert instead of here.");
        }

        if (options.PendingOccurrenceLeaseDuration <= TimeSpan.Zero)
        {
            throw new JobConfigurationException(
                $"WorkerKitOptions.PendingOccurrenceLeaseDuration must be positive, got " +
                $"{options.PendingOccurrenceLeaseDuration}. A zero or negative lease is expired " +
                "the instant it is acquired, so a second host could acquire the same occurrence " +
                "immediately — duplicate execution by configuration.");
        }

        if (options.LockAcquireTimeout < TimeSpan.Zero)
        {
            throw new JobConfigurationException(
                $"WorkerKitOptions.LockAcquireTimeout must be zero or positive, got {options.LockAcquireTimeout}.");
        }

        if (options.ShutdownGracePeriod < TimeSpan.Zero)
        {
            throw new JobConfigurationException(
                $"WorkerKitOptions.ShutdownGracePeriod must be zero or positive, got {options.ShutdownGracePeriod}.");
        }
    }
}

namespace ResilientWorkerKit;

/// <summary>Classification of a job execution failure.</summary>
public enum JobFailureKind
{
    /// <summary>A temporary condition (network blip, 5xx, timeout of a dependency). Eligible for retry.</summary>
    Transient = 0,

    /// <summary>A deterministic failure (validation error, unsupported payload, domain rule). Never retried.</summary>
    Permanent = 1,

    /// <summary>The execution observed cooperative cancellation (host shutdown or manual stop). Not an error.</summary>
    Cancelled = 2,

    /// <summary>The total execution timeout elapsed.</summary>
    TimedOut = 3,

    /// <summary>The execution was found still running after a process crash/restart and cannot have survived.</summary>
    Abandoned = 4,

    /// <summary>The job or its configuration is invalid (bad time zone, corrupted checkpoint...). Never retried.</summary>
    Misconfigured = 5,
}

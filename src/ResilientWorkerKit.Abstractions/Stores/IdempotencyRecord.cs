namespace ResilientWorkerKit;

/// <summary>Lifecycle status of an idempotency record.</summary>
public enum IdempotencyStatus
{
    /// <summary>The key was acquired; the side effect is (presumably) in progress.</summary>
    Pending = 0,

    /// <summary>The side effect completed; the key must not be processed again until it expires.</summary>
    Completed = 1,

    /// <summary>The side effect failed; the key may be re-acquired.</summary>
    Failed = 2,
}

/// <summary>
/// One idempotency record. Keys must be stable business identities
/// (e.g. <c>reservation:41:7</c> = entity:id:version) and must not contain personal data.
/// </summary>
public sealed class IdempotencyRecord
{
    /// <summary>The idempotency key (unique per job).</summary>
    public required string Key { get; init; }

    /// <summary>The owning job.</summary>
    public required string JobId { get; init; }

    /// <summary>Current status.</summary>
    public IdempotencyStatus Status { get; set; } = IdempotencyStatus.Pending;

    /// <summary>The execution that most recently acquired the key.</summary>
    public string? ExecutionId { get; set; }

    /// <summary>Creation time (UTC).</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Completion time (UTC), when completed.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>Optional expiry; an expired record behaves as if absent.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

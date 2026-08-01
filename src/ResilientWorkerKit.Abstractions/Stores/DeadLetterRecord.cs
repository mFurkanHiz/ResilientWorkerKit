namespace ResilientWorkerKit;

/// <summary>
/// A dead-letter entry: an item or execution that could not be processed after retries.
/// Payload content must be masked/summarized — never raw bodies with secrets or personal data.
/// </summary>
public sealed class DeadLetterRecord
{
    /// <summary>Unique id of the record.</summary>
    public required string Id { get; init; }

    /// <summary>The owning job.</summary>
    public required string JobId { get; init; }

    /// <summary>The execution during which the failure occurred.</summary>
    public required string ExecutionId { get; init; }

    /// <summary><c>execution</c> for execution-level entries, <c>item</c> for item-level entries.</summary>
    public required string Scope { get; init; }

    /// <summary>Safe identifier of the failed item (e.g. <c>reservation:41</c>); null for execution scope.</summary>
    public string? ItemId { get; init; }

    /// <summary>Failure classification, when known.</summary>
    public JobFailureKind? FailureKind { get; init; }

    /// <summary>Sanitized failure description.</summary>
    public required string Reason { get; init; }

    /// <summary>Number of attempts made before dead-lettering.</summary>
    public int AttemptCount { get; init; }

    /// <summary>Optional masked payload summary or reference (never the raw payload).</summary>
    public string? PayloadSummary { get; init; }

    /// <summary>Creation time (UTC).</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Set when the entry was later reprocessed successfully.</summary>
    public DateTimeOffset? ReprocessedAtUtc { get; set; }
}

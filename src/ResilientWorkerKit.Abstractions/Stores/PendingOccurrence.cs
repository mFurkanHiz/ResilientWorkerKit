namespace ResilientWorkerKit;

/// <summary>
/// A job occurrence that has been planned durably rather than derived from the job's schedule.
/// <para>
/// v1.1 writes these for follow-up retries — "this failed, run it again in five minutes" —
/// which is what makes a planned action survive a process restart during its retry window.
/// The shape is deliberately generic (<see cref="Source"/>, <see cref="PayloadJson"/>) so that
/// runtime-created triggers can use the same queue and the same scheduler path later.
/// </para>
/// </summary>
public sealed class PendingOccurrence
{
    /// <summary>Unique id of the queued occurrence.</summary>
    public required string Id { get; init; }

    /// <summary>The job to run.</summary>
    public required string JobId { get; init; }

    /// <summary>When the occurrence becomes due (UTC).</summary>
    public required DateTimeOffset DueAtUtc { get; init; }

    /// <summary>
    /// Occurrence identity token, combined with the job id to form the ScheduledExecutionId.
    /// Must be unique per logical occurrence so duplicate-execution suppression keeps working.
    /// </summary>
    public required string IdentityToken { get; init; }

    /// <summary>What planned this occurrence — <c>follow-up-retry</c> today.</summary>
    public string Source { get; init; } = PendingOccurrenceSources.FollowUpRetry;

    /// <summary>The occurrence this one follows up on, when it is a retry.</summary>
    public string? OriginScheduledExecutionId { get; init; }

    /// <summary>1-based follow-up ordinal: 1 is the first retry after the original failure.</summary>
    public int FollowUpOrdinal { get; init; }

    /// <summary>
    /// Optional state for the occurrence. Unused by follow-up retries; reserved so that
    /// runtime-created triggers can carry their own payload without a schema change.
    /// Must never contain secrets or personal data (see docs/security.md).
    /// </summary>
    public string? PayloadJson { get; init; }

    /// <summary>When the occurrence was queued (UTC).</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>Well-known <see cref="PendingOccurrence.Source"/> values.</summary>
public static class PendingOccurrenceSources
{
    /// <summary>Queued by the engine after an execution failed and a follow-up retry remains.</summary>
    public const string FollowUpRetry = "follow-up-retry";
}

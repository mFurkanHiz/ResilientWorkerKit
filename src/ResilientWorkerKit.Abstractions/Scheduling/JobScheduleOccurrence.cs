namespace ResilientWorkerKit;

/// <summary>One planned occurrence of a job schedule.</summary>
/// <param name="ScheduledAtUtc">The planned execution time in UTC.</param>
/// <param name="ScheduledLocalTime">The planned execution time expressed in the job's time zone.</param>
/// <param name="IdentityToken">
/// Deterministic identity of the occurrence within its job (e.g. <c>2026-08</c> for a monthly
/// schedule, an ISO timestamp for interval schedules). Combined with the job id it forms the
/// <c>ScheduledExecutionId</c> used to prevent duplicate execution of the same occurrence
/// across restarts, misfire recovery and DST transitions.
/// </param>
public sealed record JobScheduleOccurrence(
    DateTimeOffset ScheduledAtUtc,
    DateTime ScheduledLocalTime,
    string IdentityToken);

namespace ResilientWorkerKit;

/// <summary>Inputs available to <see cref="IJobSchedule.GetOccurrenceAfter"/>.</summary>
/// <param name="NowUtc">The current time (from <see cref="TimeProvider"/>, never the wall clock directly).</param>
/// <param name="LastCompletedAtUtc">
/// Completion time of the job's most recent execution, if any. Used by fixed-delay schedules,
/// which anchor the next occurrence to the previous completion rather than the previous start.
/// </param>
/// <param name="TimeZone">The job's time zone (defaults to UTC when the job does not configure one).</param>
public sealed record ScheduleCalculationContext(
    DateTimeOffset NowUtc,
    DateTimeOffset? LastCompletedAtUtc,
    TimeZoneInfo TimeZone);

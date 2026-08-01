namespace ResilientWorkerKit;

/// <summary>
/// Computes schedule occurrences for a job. Implementations are pure functions of the inputs
/// (no wall-clock access) so they are fully testable.
/// </summary>
public interface IJobSchedule
{
    /// <summary>
    /// Returns the earliest occurrence whose scheduled time (UTC) is strictly after
    /// <paramref name="afterUtc"/>, or <see langword="null"/> if the schedule produces no
    /// further occurrences (e.g. a one-time schedule that already fired).
    /// The returned occurrence may lie in the past relative to <see cref="ScheduleCalculationContext.NowUtc"/>;
    /// the engine detects that as a misfire and applies the configured misfire policy.
    /// </summary>
    /// <param name="afterUtc">Exclusive lower bound, normally the previously handled occurrence.</param>
    /// <param name="context">Additional inputs: current time, last completion, time zone.</param>
    JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context);

    /// <summary>Human-readable description used in logs and health output (e.g. "every 00:05:00").</summary>
    string Describe();
}

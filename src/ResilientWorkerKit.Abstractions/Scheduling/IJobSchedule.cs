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

    /// <summary>
    /// Whether a host starting with no execution history should look for occurrences that are
    /// already in the past, instead of only scheduling forward from now.
    /// <para>
    /// Recurring schedules leave this <see langword="false"/>: a new deployment of an hourly job
    /// should not try to replay every hour since the epoch. Finite, planned schedules — a single
    /// instant, an explicit list of instants — return <see langword="true"/>, because those
    /// occurrences exist precisely so they happen, and a host that was down at that minute must
    /// still see them and apply its misfire policy.
    /// </para>
    /// </summary>
    bool DiscoverPastOccurrencesOnFirstStart => false;
}

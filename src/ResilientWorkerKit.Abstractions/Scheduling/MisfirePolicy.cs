namespace ResilientWorkerKit;

/// <summary>
/// What to do when a schedule occurrence was missed (host down, maintenance, long-running
/// previous execution). See docs/scheduling.md for per-schedule-type support.
/// </summary>
public enum MisfirePolicy
{
    /// <summary>Do not run the missed occurrence; wait for the next regular one. The safe default.</summary>
    Skip = 0,

    /// <summary>
    /// Run the most recently missed occurrence exactly once (keeping its original identity),
    /// then return to the regular schedule. Restart-safe: the same missed occurrence is never
    /// created twice.
    /// </summary>
    RunImmediatelyOnce = 1,

    /// <summary>
    /// Like <see cref="RunImmediatelyOnce"/>, but only when the occurrence is late by no more
    /// than the configured tolerance; older occurrences are skipped.
    /// </summary>
    RunIfWithinTolerance = 2,

    /// <summary>
    /// Forget the missed occurrence and re-anchor the schedule to the current time
    /// (interval/fixed-delay only; meaningless for calendar schedules).
    /// </summary>
    RescheduleFromNow = 3,
}

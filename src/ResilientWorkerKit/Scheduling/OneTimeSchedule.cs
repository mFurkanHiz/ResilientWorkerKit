using System.Globalization;

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Fires exactly once at a fixed instant. After the occurrence has been handled the schedule
/// produces no further occurrences; a successfully completed occurrence is never re-triggered,
/// even across restarts (occurrence-identity check).
/// </summary>
public sealed class OneTimeSchedule : IJobSchedule
{
    /// <summary>Creates the schedule.</summary>
    public OneTimeSchedule(DateTimeOffset runAtUtc) => RunAtUtc = runAtUtc.ToUniversalTime();

    /// <summary>The single planned instant (UTC).</summary>
    public DateTimeOffset RunAtUtc { get; }

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (RunAtUtc <= afterUtc)
        {
            return null;
        }

        return new JobScheduleOccurrence(
            RunAtUtc,
            LocalTimeConverter.ToLocal(RunAtUtc, context.TimeZone),
            "once:" + RunAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public bool DiscoverPastOccurrencesOnFirstStart => true;

    /// <inheritdoc />
    public string Describe() => $"once at {RunAtUtc:u}";
}

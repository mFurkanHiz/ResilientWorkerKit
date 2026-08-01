using System.Globalization;

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Fires at a fixed rate: each occurrence is anchored to the previous *scheduled* time
/// (<c>next = previous + interval</c>), regardless of how long the execution took.
/// Compare with <see cref="FixedDelaySchedule"/>, which anchors to the previous *completion*.
/// </summary>
public sealed class IntervalSchedule : IJobSchedule
{
    /// <summary>Creates the schedule.</summary>
    /// <param name="interval">The fixed rate; must be positive.</param>
    public IntervalSchedule(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new JobConfigurationException($"Interval must be positive, got {interval}.");
        }

        Interval = interval;
    }

    /// <summary>The fixed rate.</summary>
    public TimeSpan Interval { get; }

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scheduledUtc = afterUtc + Interval;
        return new JobScheduleOccurrence(
            scheduledUtc,
            LocalTimeConverter.ToLocal(scheduledUtc, context.TimeZone),
            scheduledUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public string Describe() => $"every {Interval}";
}

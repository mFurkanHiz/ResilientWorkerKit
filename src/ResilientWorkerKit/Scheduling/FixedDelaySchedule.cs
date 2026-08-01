using System.Globalization;

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Fires a fixed delay after the previous execution *completed*
/// (<c>next = previous completion + delay</c>), so slow executions never overlap their
/// successors and the effective period is execution time + delay.
/// Compare with <see cref="IntervalSchedule"/>, which fires at a fixed rate.
/// </summary>
public sealed class FixedDelaySchedule : IJobSchedule
{
    /// <summary>Creates the schedule.</summary>
    /// <param name="delay">Delay after each completion; must be positive.</param>
    public FixedDelaySchedule(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            throw new JobConfigurationException($"Fixed delay must be positive, got {delay}.");
        }

        Delay = delay;
    }

    /// <summary>The delay after each completion.</summary>
    public TimeSpan Delay { get; }

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var anchor = context.LastCompletedAtUtc is { } completed && completed > afterUtc
            ? completed
            : afterUtc;
        var scheduledUtc = anchor + Delay;
        return new JobScheduleOccurrence(
            scheduledUtc,
            LocalTimeConverter.ToLocal(scheduledUtc, context.TimeZone),
            scheduledUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public string Describe() => $"{Delay} after each completion";
}

using System.Globalization;

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Fires at an explicit set of instants — "10:00 and 14:00 on 15 August, and 10:00 on
/// 16 August" — rather than at a repeating pattern. Use it for planned one-off actions whose
/// times are known in advance: a sale opening, a campaign start, a migration cut-over.
/// <para>
/// Each instant carries its own occurrence identity, so a completed one is never repeated
/// after a restart, and instants already in the past when the host starts are handled by the
/// job's misfire policy like any other missed occurrence.
/// </para>
/// </summary>
public sealed class ExplicitTimesSchedule : IJobSchedule
{
    private readonly DateTimeOffset[] _times;

    /// <summary>Creates the schedule from at least one instant. Order does not matter.</summary>
    /// <exception cref="JobConfigurationException">
    /// No instants were supplied, an instant appears more than once, or two distinct instants
    /// fall within the same UTC second. A planned action must run or fail loudly; both
    /// duplicate cases would otherwise lose an occurrence silently — a duplicate by collapsing
    /// it, a same-second pair because occurrence identity has second precision, so the second
    /// instant would be skipped as already completed.
    /// </exception>
    public ExplicitTimesSchedule(IEnumerable<DateTimeOffset> times)
    {
        ArgumentNullException.ThrowIfNull(times);

        _times = times
            .Select(t => t.ToUniversalTime())
            .OrderBy(t => t)
            .ToArray();

        if (_times.Length == 0)
        {
            throw new JobConfigurationException(
                "An explicit-times schedule needs at least one instant.");
        }

        for (var i = 1; i < _times.Length; i++)
        {
            if (_times[i] == _times[i - 1])
            {
                throw new JobConfigurationException(
                    $"The instant {_times[i]:u} appears more than once. Each instant is one " +
                    "planned occurrence, so a duplicate is a configuration error rather than " +
                    "a request to run twice.");
            }

            if (SecondFloor(_times[i]) == SecondFloor(_times[i - 1]))
            {
                throw new JobConfigurationException(
                    $"The instants {_times[i - 1]:o} and {_times[i]:o} fall within the same " +
                    "UTC second. Occurrence identity has second precision, so the later one " +
                    "would be silently skipped as a duplicate; use instants at least one " +
                    "second apart.");
            }
        }
    }

    private static long SecondFloor(DateTimeOffset t) => t.UtcTicks / TimeSpan.TicksPerSecond;

    /// <summary>The configured instants, in ascending UTC order.</summary>
    public IReadOnlyList<DateTimeOffset> Times => _times;

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var time in _times)
        {
            if (time > afterUtc)
            {
                return new JobScheduleOccurrence(
                    time,
                    LocalTimeConverter.ToLocal(time, context.TimeZone),
                    "at:" + time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }
        }

        return null;
    }

    /// <inheritdoc />
    public bool DiscoverPastOccurrencesOnFirstStart => true;

    /// <inheritdoc />
    public string Describe()
        => _times.Length == 1
            ? $"once at {_times[0]:u}"
            : $"at {_times.Length} explicit times from {_times[0]:u} to {_times[^1]:u}";
}

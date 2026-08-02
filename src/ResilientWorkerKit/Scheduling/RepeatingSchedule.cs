using System.Globalization;

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Runs a fixed number of times, starting at a given instant and repeating at a fixed gap —
/// "three times on the 15th, four hours apart". Occurrences are computed on demand from the
/// arithmetic progression rather than materialized into an array, so the count has no
/// practical upper bound and construction is O(1) regardless of it.
/// <para>
/// Identities use the same <c>at:&lt;instant&gt;</c> form as
/// <see cref="ExplicitTimesSchedule"/>, so a completed run is never repeated after a restart,
/// and a job rewritten between <c>AtTimes</c> and <c>Repeating</c> over the same instants
/// keeps matching its history.
/// </para>
/// </summary>
public sealed class RepeatingSchedule : IJobSchedule
{
    private readonly DateTimeOffset _startUtc;
    private readonly TimeSpan _every;
    private readonly int _count;

    /// <summary>Creates the schedule.</summary>
    /// <param name="startAt">The first instant.</param>
    /// <param name="every">
    /// Gap between instants. At least one second, because occurrence identity has second
    /// precision and closer instants would collide.
    /// </param>
    /// <param name="count">Total number of runs; at least 1.</param>
    /// <exception cref="JobConfigurationException">
    /// The gap or count is out of range, or the progression would run past the end of
    /// representable time. Checked here so a bad configuration fails at registration instead
    /// of overflowing mid-run.
    /// </exception>
    public RepeatingSchedule(DateTimeOffset startAt, TimeSpan every, int count)
    {
        if (every < TimeSpan.FromSeconds(1))
        {
            throw new JobConfigurationException(
                $"The repeat interval must be at least one second, got {every}. Occurrence " +
                "identity has second precision, so closer instants would collide.");
        }

        if (count < 1)
        {
            throw new JobConfigurationException($"The repeat count must be at least 1, got {count}.");
        }

        _startUtc = startAt.ToUniversalTime();
        _every = every;
        _count = count;

        var maxSpanTicks = (DateTimeOffset.MaxValue - _startUtc).Ticks;
        if (count > 1 && _every.Ticks > maxSpanTicks / (count - 1))
        {
            throw new JobConfigurationException(
                $"{count} runs every {every} starting {_startUtc:u} would run past the end of " +
                "representable time.");
        }
    }

    /// <summary>The first instant (UTC).</summary>
    public DateTimeOffset StartAtUtc => _startUtc;

    /// <summary>Gap between instants.</summary>
    public TimeSpan Every => _every;

    /// <summary>Total number of runs.</summary>
    public int Count => _count;

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var index = afterUtc < _startUtc
            ? 0L
            : ((afterUtc - _startUtc).Ticks / _every.Ticks) + 1;

        if (index >= _count)
        {
            return null;
        }

        var time = _startUtc + TimeSpan.FromTicks(_every.Ticks * index);
        return new JobScheduleOccurrence(
            time,
            LocalTimeConverter.ToLocal(time, context.TimeZone),
            "at:" + time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public bool DiscoverPastOccurrencesOnFirstStart => true;

    /// <inheritdoc />
    public string Describe()
        => _count == 1
            ? $"once at {_startUtc:u}"
            : $"{_count} runs every {_every} from {_startUtc:u}";
}

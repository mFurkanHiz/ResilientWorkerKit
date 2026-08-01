using System.Globalization;

namespace ResilientWorkerKit.Scheduling;

/// <summary>Fires on the selected weekdays at a fixed local time in the job's time zone.</summary>
public sealed class WeeklySchedule : IJobSchedule
{
    private readonly HashSet<DayOfWeek> _days;

    /// <summary>Creates the schedule.</summary>
    /// <param name="days">At least one weekday.</param>
    /// <param name="time">The local wall-clock time.</param>
    public WeeklySchedule(IEnumerable<DayOfWeek> days, TimeOnly time)
    {
        ArgumentNullException.ThrowIfNull(days);
        _days = new HashSet<DayOfWeek>(days);
        if (_days.Count == 0)
        {
            throw new JobConfigurationException("A weekly schedule needs at least one day of the week.");
        }

        Time = time;
    }

    /// <summary>The local wall-clock time.</summary>
    public TimeOnly Time { get; }

    /// <summary>The selected weekdays.</summary>
    public IReadOnlyCollection<DayOfWeek> Days => _days;

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var date = DateOnly.FromDateTime(LocalTimeConverter.ToLocal(afterUtc, context.TimeZone));

        for (var i = 0; i < 10; i++)
        {
            if (_days.Contains(date.DayOfWeek))
            {
                var local = date.ToDateTime(Time);
                var utc = LocalTimeConverter.ToUtc(local, context.TimeZone);
                if (utc > afterUtc)
                {
                    return new JobScheduleOccurrence(
                        utc,
                        local,
                        local.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
                }
            }

            date = date.AddDays(1);
        }

        throw new InvalidOperationException("Weekly schedule failed to converge; this is a bug.");
    }

    /// <inheritdoc />
    public string Describe()
        => $"weekly on {string.Join(",", _days.OrderBy(d => d))} at {Time:HH\\:mm}";
}

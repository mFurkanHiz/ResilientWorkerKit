using System.Globalization;

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Fires on the actual last day of every month (Feb 28/29, Apr 30, May 31, ...) at a fixed
/// local time. Occurrence identity is <c>yyyy-MM</c> — one completed execution per month.
/// </summary>
public sealed class LastDayOfMonthSchedule : IJobSchedule
{
    /// <summary>Creates the schedule.</summary>
    public LastDayOfMonthSchedule(TimeOnly time) => Time = time;

    /// <summary>The local wall-clock time.</summary>
    public TimeOnly Time { get; }

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var localAfter = LocalTimeConverter.ToLocal(afterUtc, context.TimeZone);
        var year = localAfter.Year;
        var month = localAfter.Month;

        for (var i = 0; i < 3; i++)
        {
            var day = DateTime.DaysInMonth(year, month);
            var local = new DateTime(year, month, day, Time.Hour, Time.Minute, Time.Second, DateTimeKind.Unspecified);
            var utc = LocalTimeConverter.ToUtc(local, context.TimeZone);
            if (utc > afterUtc)
            {
                return new JobScheduleOccurrence(
                    utc,
                    local,
                    string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{month:D2}"));
            }

            (year, month) = month == 12 ? (year + 1, 1) : (year, month + 1);
        }

        throw new InvalidOperationException("Last-day-of-month schedule failed to converge; this is a bug.");
    }

    /// <inheritdoc />
    public string Describe() => $"on the last day of each month at {Time:HH\\:mm}";
}

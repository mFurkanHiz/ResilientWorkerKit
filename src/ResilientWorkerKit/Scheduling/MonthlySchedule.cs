using System.Globalization;

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Fires once per month on a fixed day at a fixed local time. Months lacking the configured
/// day (29–31) behave according to <see cref="MonthlyInvalidDayPolicy"/>. The occurrence
/// identity is <c>yyyy-MM</c>, which guarantees at most one completed execution per month
/// across restarts.
/// </summary>
public sealed class MonthlySchedule : IJobSchedule
{
    /// <summary>Creates the schedule.</summary>
    /// <param name="dayOfMonth">1–31.</param>
    /// <param name="time">The local wall-clock time.</param>
    /// <param name="invalidDayPolicy">Behavior for months lacking the day.</param>
    public MonthlySchedule(int dayOfMonth, TimeOnly time, MonthlyInvalidDayPolicy invalidDayPolicy = MonthlyInvalidDayPolicy.SkipMonth)
    {
        if (dayOfMonth is < 1 or > 31)
        {
            throw new JobConfigurationException($"Day of month must be 1–31, got {dayOfMonth}.");
        }

        if (invalidDayPolicy == MonthlyInvalidDayPolicy.FailConfiguration && dayOfMonth > 28)
        {
            throw new JobConfigurationException(
                $"Day {dayOfMonth} does not exist in every month. Choose a day up to 28, or use " +
                $"{nameof(MonthlyInvalidDayPolicy.SkipMonth)} / {nameof(MonthlyInvalidDayPolicy.RunOnLastAvailableDay)} " +
                "to state explicitly what short months should do.");
        }

        DayOfMonth = dayOfMonth;
        Time = time;
        InvalidDayPolicy = invalidDayPolicy;
    }

    /// <summary>The configured day (1–31).</summary>
    public int DayOfMonth { get; }

    /// <summary>The local wall-clock time.</summary>
    public TimeOnly Time { get; }

    /// <summary>Behavior for months lacking the configured day.</summary>
    public MonthlyInvalidDayPolicy InvalidDayPolicy { get; }

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var localAfter = LocalTimeConverter.ToLocal(afterUtc, context.TimeZone);
        var year = localAfter.Year;
        var month = localAfter.Month;

        for (var i = 0; i < 26; i++)
        {
            var daysInMonth = DateTime.DaysInMonth(year, month);
            int? day = DayOfMonth <= daysInMonth
                ? DayOfMonth
                : InvalidDayPolicy == MonthlyInvalidDayPolicy.RunOnLastAvailableDay ? daysInMonth : null;

            if (day is { } d)
            {
                var local = new DateTime(year, month, d, Time.Hour, Time.Minute, Time.Second, DateTimeKind.Unspecified);
                var utc = LocalTimeConverter.ToUtc(local, context.TimeZone);
                if (utc > afterUtc)
                {
                    return new JobScheduleOccurrence(utc, local, FormatIdentity(year, month));
                }
            }

            (year, month) = month == 12 ? (year + 1, 1) : (year, month + 1);
        }

        throw new InvalidOperationException("Monthly schedule failed to converge; this is a bug.");
    }

    /// <inheritdoc />
    public string Describe()
        => $"monthly on day {DayOfMonth} at {Time:HH\\:mm} ({InvalidDayPolicy})";

    private static string FormatIdentity(int year, int month)
        => string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{month:D2}");
}

using System.Globalization;

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Fires every day at a fixed local time in the job's time zone. Spring-forward gaps shift
/// the occurrence to the end of the gap; the fall-back hour fires only once (see
/// <see cref="LocalTimeConverter"/>).
/// </summary>
public sealed class DailySchedule : IJobSchedule
{
    /// <summary>Creates the schedule.</summary>
    public DailySchedule(TimeOnly time) => Time = time;

    /// <summary>The local wall-clock time.</summary>
    public TimeOnly Time { get; }

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var date = DateOnly.FromDateTime(LocalTimeConverter.ToLocal(afterUtc, context.TimeZone));

        // Start from the local date of `afterUtc` and walk forward until the converted
        // instant is strictly after it (at most a couple of iterations plus DST slack).
        for (var i = 0; i < 4; i++)
        {
            var local = date.ToDateTime(Time);
            var utc = LocalTimeConverter.ToUtc(local, context.TimeZone);
            if (utc > afterUtc)
            {
                return new JobScheduleOccurrence(utc, local, FormatIdentity(local));
            }

            date = date.AddDays(1);
        }

        throw new InvalidOperationException("Daily schedule failed to converge; this is a bug.");
    }

    /// <inheritdoc />
    public string Describe() => $"daily at {Time:HH\\:mm}";

    private static string FormatIdentity(DateTime local)
        => local.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
}

using ResilientWorkerKit.Scheduling;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Scheduling;

public class CalendarScheduleTests
{
    // ---- Daily ---------------------------------------------------------------------------

    [Fact]
    public void Daily_ConvertsLocalTimeToUtc()
    {
        // 02:00 Europe/Istanbul (UTC+3, no DST) == 23:00 UTC the previous day.
        var schedule = new DailySchedule(new TimeOnly(2, 0));
        var after = Times.Utc(2026, 8, 1, 10, 0);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after, "Europe/Istanbul"));

        Assert.Equal(Times.Utc(2026, 8, 1, 23, 0), occurrence!.ScheduledAtUtc);
        Assert.Equal(new DateTime(2026, 8, 2, 2, 0, 0), occurrence.ScheduledLocalTime);
        Assert.Equal("2026-08-02T02:00", occurrence.IdentityToken);
    }

    [Fact]
    public void Daily_SpringForwardGap_ShiftsToEndOfGap()
    {
        // Europe/Berlin, 2026-03-29: 02:00–03:00 does not exist. A 02:30 schedule runs at
        // 03:00 local (01:00 UTC) instead of silently skipping the day.
        var schedule = new DailySchedule(new TimeOnly(2, 30));
        var after = Times.Utc(2026, 3, 29, 0, 0);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after, "Europe/Berlin"));

        Assert.Equal(Times.Utc(2026, 3, 29, 1, 0), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void Daily_FallBackAmbiguousTime_FiresOnlyOnce()
    {
        // Europe/Berlin, 2026-10-25: 02:30 occurs twice. The schedule fires the first
        // occurrence (offset +02:00, i.e. 00:30 UTC) and the next occurrence is the NEXT day —
        // never the second 02:30 of the same day.
        var schedule = new DailySchedule(new TimeOnly(2, 30));
        var after = Times.Utc(2026, 10, 24, 12, 0);
        var context = Times.Context(after, "Europe/Berlin");

        var first = schedule.GetOccurrenceAfter(after, context)!;
        var second = schedule.GetOccurrenceAfter(first.ScheduledAtUtc, context)!;

        Assert.Equal(Times.Utc(2026, 10, 25, 0, 30), first.ScheduledAtUtc);
        Assert.Equal(new DateTime(2026, 10, 26, 2, 30, 0), second.ScheduledLocalTime);
        Assert.Equal(Times.Utc(2026, 10, 26, 1, 30), second.ScheduledAtUtc);
    }

    // ---- Weekly --------------------------------------------------------------------------

    [Fact]
    public void Weekly_PicksTheNextConfiguredDay()
    {
        // Mon/Wed/Fri 09:30 Istanbul; 2026-08-04 is a Tuesday → next is Wednesday the 5th.
        var schedule = new WeeklySchedule([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], new TimeOnly(9, 30));
        var after = Times.Utc(2026, 8, 4, 12, 0);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after, "Europe/Istanbul"));

        Assert.Equal(DayOfWeek.Wednesday, occurrence!.ScheduledLocalTime.DayOfWeek);
        Assert.Equal(Times.Utc(2026, 8, 5, 6, 30), occurrence.ScheduledAtUtc);
    }

    [Fact]
    public void Weekly_SameDayLaterTime_FiresSameDay()
    {
        // Sunday 03:00 Istanbul == Saturday 00:00 UTC. 2026-08-02 is a Sunday.
        var schedule = new WeeklySchedule([DayOfWeek.Sunday], new TimeOnly(3, 0));
        var after = Times.Utc(2026, 8, 1, 10, 0);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after, "Europe/Istanbul"));

        Assert.Equal(Times.Utc(2026, 8, 2, 0, 0), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void Weekly_RequiresAtLeastOneDay()
    {
        Assert.Throws<JobConfigurationException>(() => new WeeklySchedule([], new TimeOnly(9, 0)));
    }

    // ---- Cron ----------------------------------------------------------------------------

    [Fact]
    public void Cron_FiveField_ComputesNextOccurrence()
    {
        var schedule = new CronSchedule("*/5 * * * *");
        var after = Times.Utc(2026, 8, 1, 10, 2);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after));

        Assert.Equal(Times.Utc(2026, 8, 1, 10, 5), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void Cron_SixField_SupportsSeconds()
    {
        var schedule = new CronSchedule("30 * * * * *");
        var after = Times.Utc(2026, 8, 1, 10, 0, 0);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after));

        Assert.Equal(Times.Utc(2026, 8, 1, 10, 0, 30), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void Cron_EvaluatesInTheJobTimeZone()
    {
        // "0 2 * * *" in Istanbul == 23:00 UTC.
        var schedule = new CronSchedule("0 2 * * *");
        var after = Times.Utc(2026, 8, 1, 10, 0);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after, "Europe/Istanbul"));

        Assert.Equal(Times.Utc(2026, 8, 1, 23, 0), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void Cron_InvalidExpression_FailsConfiguration()
    {
        Assert.Throws<JobConfigurationException>(() => new CronSchedule("not a cron"));
    }

    // ---- One-time ------------------------------------------------------------------------

    [Fact]
    public void OneTime_FiresOnceThenNever()
    {
        var runAt = Times.Utc(2026, 9, 1, 12, 0);
        var schedule = new OneTimeSchedule(runAt);
        var context = Times.Context(Times.Utc(2026, 8, 1));

        var occurrence = schedule.GetOccurrenceAfter(Times.Utc(2026, 8, 1), context);
        var afterFiring = schedule.GetOccurrenceAfter(runAt, context);

        Assert.Equal(runAt, occurrence!.ScheduledAtUtc);
        Assert.Null(afterFiring);
    }
}

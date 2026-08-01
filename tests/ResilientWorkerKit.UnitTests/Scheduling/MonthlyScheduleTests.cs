using ResilientWorkerKit.Scheduling;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Scheduling;

public class MonthlyScheduleTests
{
    [Fact]
    public void Day5_At1030_Istanbul_ComputesCorrectUtc()
    {
        var schedule = new MonthlySchedule(5, new TimeOnly(10, 30));
        var after = Times.Utc(2026, 8, 1);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after, "Europe/Istanbul"));

        Assert.Equal(Times.Utc(2026, 8, 5, 7, 30), occurrence!.ScheduledAtUtc);
        Assert.Equal(new DateTime(2026, 8, 5, 10, 30, 0), occurrence.ScheduledLocalTime);
        Assert.Equal("2026-08", occurrence.IdentityToken);
    }

    [Fact]
    public void OccurrenceIsStrictlyAfter_SoTheSameMonthNeverRepeats()
    {
        var schedule = new MonthlySchedule(5, new TimeOnly(10, 30));
        var context = Times.Context(Times.Utc(2026, 8, 5, 8, 0), "Europe/Istanbul");

        var august = schedule.GetOccurrenceAfter(Times.Utc(2026, 8, 1), context)!;
        var next = schedule.GetOccurrenceAfter(august.ScheduledAtUtc, context)!;

        Assert.Equal("2026-08", august.IdentityToken);
        Assert.Equal("2026-09", next.IdentityToken);
        Assert.Equal(Times.Utc(2026, 9, 5, 7, 30), next.ScheduledAtUtc);
    }

    [Fact]
    public void Day31_SkipMonth_SkipsShortMonths()
    {
        var schedule = new MonthlySchedule(31, new TimeOnly(12, 0), MonthlyInvalidDayPolicy.SkipMonth);
        var context = Times.Context(Times.Utc(2026, 1, 15));

        // After January 31: February (28d), April (30d) etc. are skipped.
        var afterJanuary = schedule.GetOccurrenceAfter(Times.Utc(2026, 1, 31, 12, 0), context)!;
        var afterMarch = schedule.GetOccurrenceAfter(afterJanuary.ScheduledAtUtc, context)!;

        Assert.Equal(Times.Utc(2026, 3, 31, 12, 0), afterJanuary.ScheduledAtUtc);
        Assert.Equal("2026-03", afterJanuary.IdentityToken);
        Assert.Equal(Times.Utc(2026, 5, 31, 12, 0), afterMarch.ScheduledAtUtc);
    }

    [Fact]
    public void Day31_RunOnLastAvailableDay_RunsOnFeb28()
    {
        var schedule = new MonthlySchedule(31, new TimeOnly(12, 0), MonthlyInvalidDayPolicy.RunOnLastAvailableDay);

        var occurrence = schedule.GetOccurrenceAfter(
            Times.Utc(2026, 1, 31, 12, 0), Times.Context(Times.Utc(2026, 2, 1)));

        Assert.Equal(Times.Utc(2026, 2, 28, 12, 0), occurrence!.ScheduledAtUtc);
        Assert.Equal("2026-02", occurrence.IdentityToken);
    }

    [Fact]
    public void Day31_RunOnLastAvailableDay_RunsOnFeb29InLeapYears()
    {
        var schedule = new MonthlySchedule(31, new TimeOnly(12, 0), MonthlyInvalidDayPolicy.RunOnLastAvailableDay);

        var occurrence = schedule.GetOccurrenceAfter(
            Times.Utc(2028, 1, 31, 12, 0), Times.Context(Times.Utc(2028, 2, 1)));

        Assert.Equal(Times.Utc(2028, 2, 29, 12, 0), occurrence!.ScheduledAtUtc);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    public void FailConfiguration_RejectsDaysThatDoNotExistInEveryMonth(int day)
    {
        Assert.Throws<JobConfigurationException>(
            () => new MonthlySchedule(day, new TimeOnly(12, 0), MonthlyInvalidDayPolicy.FailConfiguration));
    }

    [Fact]
    public void FailConfiguration_AcceptsDay28()
    {
        var schedule = new MonthlySchedule(28, new TimeOnly(12, 0), MonthlyInvalidDayPolicy.FailConfiguration);
        Assert.Equal(28, schedule.DayOfMonth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(-1)]
    public void RejectsOutOfRangeDays(int day)
    {
        Assert.Throws<JobConfigurationException>(() => new MonthlySchedule(day, new TimeOnly(12, 0)));
    }
}

public class LastDayOfMonthScheduleTests
{
    [Theory]
    [InlineData(2026, 2, 1, 2026, 2, 28)]  // regular February
    [InlineData(2028, 2, 1, 2028, 2, 29)]  // leap-year February
    [InlineData(2026, 4, 10, 2026, 4, 30)] // 30-day month
    [InlineData(2026, 8, 1, 2026, 8, 31)]  // 31-day month
    [InlineData(2026, 12, 31, 2027, 1, 31)] // year rollover (after Dec 31 occurrence)
    public void FiresOnTheActualLastDay(int afterYear, int afterMonth, int afterDay, int year, int month, int day)
    {
        var schedule = new LastDayOfMonthSchedule(new TimeOnly(23, 0));
        var after = Times.Utc(afterYear, afterMonth, afterDay, 23, 0);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after));

        Assert.Equal(Times.Utc(year, month, day, 23, 0), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void UsesTheJobTimeZone()
    {
        // Last day of Aug 2026 at 23:00 Istanbul == 20:00 UTC.
        var schedule = new LastDayOfMonthSchedule(new TimeOnly(23, 0));
        var after = Times.Utc(2026, 8, 1);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after, "Europe/Istanbul"));

        Assert.Equal(Times.Utc(2026, 8, 31, 20, 0), occurrence!.ScheduledAtUtc);
        Assert.Equal("2026-08", occurrence.IdentityToken);
    }
}

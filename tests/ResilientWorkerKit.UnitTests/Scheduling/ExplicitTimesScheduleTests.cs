using ResilientWorkerKit.Scheduling;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Scheduling;

public class ExplicitTimesScheduleTests
{
    [Fact]
    public void WalksThroughEveryInstantInOrder()
    {
        var t1 = Times.Utc(2026, 8, 15, 7, 0);
        var t2 = Times.Utc(2026, 8, 15, 11, 0);
        var t3 = Times.Utc(2026, 8, 16, 7, 0);
        var schedule = new ExplicitTimesSchedule([t3, t1, t2]); // deliberately unordered
        var context = Times.Context(Times.Utc(2026, 8, 1));

        var first = schedule.GetOccurrenceAfter(Times.Utc(2026, 8, 1), context)!;
        var second = schedule.GetOccurrenceAfter(first.ScheduledAtUtc, context)!;
        var third = schedule.GetOccurrenceAfter(second.ScheduledAtUtc, context)!;
        var beyond = schedule.GetOccurrenceAfter(third.ScheduledAtUtc, context);

        Assert.Equal(t1, first.ScheduledAtUtc);
        Assert.Equal(t2, second.ScheduledAtUtc);
        Assert.Equal(t3, third.ScheduledAtUtc);
        Assert.Null(beyond);
    }

    [Fact]
    public void EachInstantHasItsOwnIdentity()
    {
        var schedule = new ExplicitTimesSchedule(
            [Times.Utc(2026, 8, 15, 7, 0), Times.Utc(2026, 8, 15, 11, 0)]);
        var context = Times.Context(Times.Utc(2026, 8, 1));

        var first = schedule.GetOccurrenceAfter(Times.Utc(2026, 8, 1), context)!;
        var second = schedule.GetOccurrenceAfter(first.ScheduledAtUtc, context)!;

        Assert.Equal("at:2026-08-15T07:00:00Z", first.IdentityToken);
        Assert.Equal("at:2026-08-15T11:00:00Z", second.IdentityToken);
    }

    [Fact]
    public void DuplicateInstantsAreRejected()
    {
        // A planned action must run or fail loudly — silently collapsing a duplicate hides a
        // configuration mistake.
        var t = Times.Utc(2026, 8, 15, 7, 0);

        var ex = Assert.Throws<JobConfigurationException>(() => new ExplicitTimesSchedule([t, t, t]));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void SameInstantInDifferentOffsets_IsStillADuplicate()
    {
        // 10:00+03:00 and 07:00Z are the same instant.
        var local = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.FromHours(3));
        var utc = Times.Utc(2026, 8, 15, 7, 0);

        Assert.Throws<JobConfigurationException>(() => new ExplicitTimesSchedule([local, utc]));
    }

    [Fact]
    public void InstantsWithinTheSameSecond_AreRejected()
    {
        // Occurrence identity has second precision ("at:...T07:00:00Z"). Two distinct instants
        // in the same second would share an identity, and the second one would be silently
        // skipped as a duplicate after the first completes — worse than failing fast here.
        var a = Times.Utc(2026, 8, 15, 7, 0).AddMilliseconds(100);
        var b = Times.Utc(2026, 8, 15, 7, 0).AddMilliseconds(900);

        var ex = Assert.Throws<JobConfigurationException>(() => new ExplicitTimesSchedule([a, b]));
        Assert.Contains("same UTC second", ex.Message);
    }

    [Fact]
    public void InstantsInDifferentSeconds_AreAccepted()
    {
        var schedule = new ExplicitTimesSchedule(
            [Times.Utc(2026, 8, 15, 7, 0, 1), Times.Utc(2026, 8, 15, 7, 0, 2)]);

        Assert.Equal(2, schedule.Times.Count);
    }

    [Fact]
    public void RequiresAtLeastOneInstant()
        => Assert.Throws<JobConfigurationException>(() => new ExplicitTimesSchedule([]));

    [Fact]
    public void PastInstantsAreStillReturned_SoTheMisfirePolicyCanDecide()
    {
        // The engine, not the schedule, decides what to do about a missed occurrence.
        var past = Times.Utc(2026, 8, 15, 7, 0);
        var schedule = new ExplicitTimesSchedule([past]);

        var occurrence = schedule.GetOccurrenceAfter(
            Times.Utc(2026, 8, 1), Times.Context(Times.Utc(2026, 9, 1)));

        Assert.Equal(past, occurrence!.ScheduledAtUtc);
    }

    // ---- Builder surface ------------------------------------------------------------------

    [Fact]
    public void AtLocalTimes_ConvertsUsingTheJobTimeZone()
    {
        // 10:00 Europe/Istanbul (UTC+3) == 07:00 UTC.
        var definition = RunnerHarness.Definition(b => b.AtLocalTimes(
            "Europe/Istanbul",
            new DateTime(2026, 8, 15, 10, 0, 0),
            new DateTime(2026, 8, 16, 10, 0, 0)));

        var schedule = Assert.IsType<ExplicitTimesSchedule>(definition.Schedule);
        Assert.Equal(Times.Utc(2026, 8, 15, 7, 0), schedule.Times[0]);
        Assert.Equal(Times.Utc(2026, 8, 16, 7, 0), schedule.Times[1]);
        Assert.Equal("Europe/Istanbul", definition.TimeZone.Id);
    }

    [Fact]
    public void DefaultMisfirePolicy_IsRunImmediatelyOnce()
    {
        // A planned action must not be silently skipped because the host was down.
        var definition = RunnerHarness.Definition(b => b.AtTimes(Times.Utc(2026, 8, 15, 7, 0)));

        Assert.Equal(MisfirePolicy.RunImmediatelyOnce, definition.MisfirePolicy);
    }

    [Fact]
    public void RescheduleFromNow_IsRejected()
        => Assert.Throws<JobConfigurationException>(() => RunnerHarness.Definition(b => b
            .AtTimes(Times.Utc(2026, 8, 15, 7, 0))
            .WithMisfirePolicy(MisfirePolicy.RescheduleFromNow)));

    [Fact]
    public void AtTimes_RejectsAnEmptySet()
        => Assert.Throws<JobConfigurationException>(() => RunnerHarness.Definition(b => b.AtTimes()));

    [Fact]
    public void PlannedSchedules_AskTheEngineToLookBackwardsOnFirstStart()
    {
        // Without this, a host starting after the planned instant would only schedule forward
        // and the occurrence would be silently lost instead of reaching the misfire policy.
        Assert.True(LooksBack(new ExplicitTimesSchedule([Times.Utc(2026, 8, 15)])));
        Assert.True(LooksBack(new OneTimeSchedule(Times.Utc(2026, 8, 15))));
    }

    [Fact]
    public void RecurringSchedules_DoNotLookBackwards()
    {
        // A new deployment of an hourly job must not try to replay every past hour.
        Assert.False(LooksBack(new IntervalSchedule(TimeSpan.FromHours(1))));
        Assert.False(LooksBack(new FixedDelaySchedule(TimeSpan.FromHours(1))));
        Assert.False(LooksBack(new DailySchedule(new TimeOnly(2, 0))));
        Assert.False(LooksBack(new WeeklySchedule([DayOfWeek.Monday], new TimeOnly(2, 0))));
        Assert.False(LooksBack(new CronSchedule("0 2 * * *")));
        Assert.False(LooksBack(new MonthlySchedule(5, new TimeOnly(10, 30))));
        Assert.False(LooksBack(new LastDayOfMonthSchedule(new TimeOnly(23, 0))));
    }

    // The flag is a default interface member, so it is read through the interface.
    private static bool LooksBack(IJobSchedule schedule) => schedule.DiscoverPastOccurrencesOnFirstStart;
}

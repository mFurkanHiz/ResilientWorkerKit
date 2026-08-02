using ResilientWorkerKit.Scheduling;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Scheduling;

public class RepeatingScheduleTests
{
    [Fact]
    public void WalksTheArithmeticProgressionInOrder()
    {
        var start = Times.Utc(2026, 8, 15, 7, 0);
        var schedule = new RepeatingSchedule(start, TimeSpan.FromHours(4), 3);
        var context = Times.Context(Times.Utc(2026, 8, 1));

        var first = schedule.GetOccurrenceAfter(Times.Utc(2026, 8, 1), context)!;
        var second = schedule.GetOccurrenceAfter(first.ScheduledAtUtc, context)!;
        var third = schedule.GetOccurrenceAfter(second.ScheduledAtUtc, context)!;
        var beyond = schedule.GetOccurrenceAfter(third.ScheduledAtUtc, context);

        Assert.Equal(start, first.ScheduledAtUtc);
        Assert.Equal(start.AddHours(4), second.ScheduledAtUtc);
        Assert.Equal(start.AddHours(8), third.ScheduledAtUtc);
        Assert.Null(beyond);
    }

    [Fact]
    public void UsesTheSameIdentityShapeAsExplicitTimes()
    {
        // Identity parity matters: occurrences completed while the job used AtTimes keep
        // matching if the job is rewritten with Repeating over the same instants.
        var schedule = new RepeatingSchedule(Times.Utc(2026, 8, 15, 7, 0), TimeSpan.FromHours(4), 2);
        var context = Times.Context(Times.Utc(2026, 8, 1));

        var first = schedule.GetOccurrenceAfter(Times.Utc(2026, 8, 1), context)!;

        Assert.Equal("at:2026-08-15T07:00:00Z", first.IdentityToken);
    }

    [Fact]
    public void AnAfterTimeBetweenInstants_ReturnsTheNextInstant()
    {
        var start = Times.Utc(2026, 8, 15, 7, 0);
        var schedule = new RepeatingSchedule(start, TimeSpan.FromHours(4), 3);
        var context = Times.Context(start);

        var occurrence = schedule.GetOccurrenceAfter(start.AddHours(1), context)!;

        Assert.Equal(start.AddHours(4), occurrence.ScheduledAtUtc);
    }

    [Fact]
    public void AfterIsStrict_AnExactInstantReturnsTheFollowingOne()
    {
        var start = Times.Utc(2026, 8, 15, 7, 0);
        var schedule = new RepeatingSchedule(start, TimeSpan.FromHours(4), 2);
        var context = Times.Context(start);

        var occurrence = schedule.GetOccurrenceAfter(start, context)!;

        Assert.Equal(start.AddHours(4), occurrence.ScheduledAtUtc);
    }

    [Fact]
    public void HugeCounts_AreComputedLazily_NotMaterialized()
    {
        // A billion occurrences would be ~16 GB as an array. The schedule is a closed-form
        // progression, so construction and lookup must both be O(1).
        var start = Times.Utc(2026, 1, 1);
        var schedule = new RepeatingSchedule(start, TimeSpan.FromSeconds(1), 1_000_000_000);
        var context = Times.Context(start);

        var last = schedule.GetOccurrenceAfter(start.AddSeconds(999_999_998), context)!;
        var beyond = schedule.GetOccurrenceAfter(last.ScheduledAtUtc, context);

        Assert.Equal(start.AddSeconds(999_999_999), last.ScheduledAtUtc);
        Assert.Null(beyond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveCounts(int count)
        => Assert.Throws<JobConfigurationException>(() =>
            new RepeatingSchedule(Times.Utc(2026, 8, 15), TimeSpan.FromHours(1), count));

    [Fact]
    public void RejectsSubSecondIntervals()
    {
        // Occurrence identity has second precision; a 500 ms gap would give two runs the same
        // identity and the second would be silently skipped.
        var ex = Assert.Throws<JobConfigurationException>(() =>
            new RepeatingSchedule(Times.Utc(2026, 8, 15), TimeSpan.FromMilliseconds(500), 3));
        Assert.Contains("second", ex.Message);
    }

    [Fact]
    public void RejectsZeroAndNegativeIntervals()
    {
        Assert.Throws<JobConfigurationException>(() =>
            new RepeatingSchedule(Times.Utc(2026, 8, 15), TimeSpan.Zero, 3));
        Assert.Throws<JobConfigurationException>(() =>
            new RepeatingSchedule(Times.Utc(2026, 8, 15), TimeSpan.FromHours(-1), 3));
    }

    [Fact]
    public void RejectsProgressionsThatOverflowRepresentableTime()
    {
        // int.MaxValue runs a day apart runs past DateTimeOffset.MaxValue. This must be a
        // configuration error at registration, not an OverflowException mid-run.
        var ex = Assert.Throws<JobConfigurationException>(() =>
            new RepeatingSchedule(Times.Utc(2026, 8, 15), TimeSpan.FromDays(1), int.MaxValue));
        Assert.Contains("representable time", ex.Message);
    }

    [Fact]
    public void AcceptsAProgressionEndingNearTheEndOfRepresentableTime()
    {
        var start = new DateTimeOffset(9999, 12, 30, 0, 0, 0, TimeSpan.Zero);
        var schedule = new RepeatingSchedule(start, TimeSpan.FromHours(1), 24);

        var occurrence = schedule.GetOccurrenceAfter(start.AddHours(22), Times.Context(start));

        Assert.Equal(start.AddHours(23), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void LooksBackwardsOnFirstStart_LikeOtherPlannedSchedules()
        => Assert.True(((IJobSchedule)new RepeatingSchedule(
            Times.Utc(2026, 8, 15), TimeSpan.FromHours(1), 3)).DiscoverPastOccurrencesOnFirstStart);

    // ---- Builder surface ------------------------------------------------------------------

    [Fact]
    public void Repeating_BuildsARepeatingSchedule()
    {
        var start = Times.Utc(2026, 8, 15, 7, 0);
        var definition = RunnerHarness.Definition(b => b.Repeating(start, TimeSpan.FromHours(4), 3));

        var schedule = Assert.IsType<RepeatingSchedule>(definition.Schedule);
        Assert.Equal(start, schedule.StartAtUtc);
        Assert.Equal(TimeSpan.FromHours(4), schedule.Every);
        Assert.Equal(3, schedule.Count);
    }

    [Fact]
    public void Repeating_DefaultMisfirePolicy_IsRunImmediatelyOnce()
    {
        var definition = RunnerHarness.Definition(b => b.Repeating(
            Times.Utc(2026, 8, 15, 7, 0), TimeSpan.FromHours(4), 3));

        Assert.Equal(MisfirePolicy.RunImmediatelyOnce, definition.MisfirePolicy);
    }

    [Fact]
    public void Repeating_RescheduleFromNow_IsRejected()
        => Assert.Throws<JobConfigurationException>(() => RunnerHarness.Definition(b => b
            .Repeating(Times.Utc(2026, 8, 15, 7, 0), TimeSpan.FromHours(4), 3)
            .WithMisfirePolicy(MisfirePolicy.RescheduleFromNow)));
}

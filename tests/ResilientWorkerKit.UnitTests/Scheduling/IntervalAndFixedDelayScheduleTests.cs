using ResilientWorkerKit.Scheduling;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Scheduling;

public class IntervalAndFixedDelayScheduleTests
{
    [Fact]
    public void Interval_ComputesNextFromPreviousScheduledTime()
    {
        var schedule = new IntervalSchedule(TimeSpan.FromMinutes(5));
        var after = Times.Utc(2026, 8, 1, 10, 0);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after));

        Assert.NotNull(occurrence);
        Assert.Equal(Times.Utc(2026, 8, 1, 10, 5), occurrence.ScheduledAtUtc);
    }

    [Fact]
    public void Interval_IgnoresCompletionTime_FixedRateSemantics()
    {
        var schedule = new IntervalSchedule(TimeSpan.FromMinutes(5));
        var after = Times.Utc(2026, 8, 1, 10, 0);
        // The previous run finished late — a fixed-rate schedule does not care.
        var context = Times.Context(after, lastCompletedAtUtc: Times.Utc(2026, 8, 1, 10, 4));

        var occurrence = schedule.GetOccurrenceAfter(after, context);

        Assert.Equal(Times.Utc(2026, 8, 1, 10, 5), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void Interval_ProducesUniqueIdentitiesPerOccurrence()
    {
        var schedule = new IntervalSchedule(TimeSpan.FromMinutes(5));
        var context = Times.Context(Times.Utc(2026, 8, 1));

        var first = schedule.GetOccurrenceAfter(Times.Utc(2026, 8, 1, 10, 0), context)!;
        var second = schedule.GetOccurrenceAfter(first.ScheduledAtUtc, context)!;

        Assert.NotEqual(first.IdentityToken, second.IdentityToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Interval_RejectsNonPositiveIntervals(int minutes)
    {
        Assert.Throws<JobConfigurationException>(() => new IntervalSchedule(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void FixedDelay_AnchorsToPreviousCompletion()
    {
        var schedule = new FixedDelaySchedule(TimeSpan.FromMinutes(10));
        var lastScheduled = Times.Utc(2026, 8, 1, 10, 0);
        // The execution started at 10:00 and finished at 10:07.
        var context = Times.Context(Times.Utc(2026, 8, 1, 10, 7), lastCompletedAtUtc: Times.Utc(2026, 8, 1, 10, 7));

        var occurrence = schedule.GetOccurrenceAfter(lastScheduled, context);

        Assert.Equal(Times.Utc(2026, 8, 1, 10, 17), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void FixedDelay_WithoutCompletionHistory_AnchorsToAfter()
    {
        var schedule = new FixedDelaySchedule(TimeSpan.FromMinutes(10));
        var after = Times.Utc(2026, 8, 1, 10, 0);

        var occurrence = schedule.GetOccurrenceAfter(after, Times.Context(after));

        Assert.Equal(Times.Utc(2026, 8, 1, 10, 10), occurrence!.ScheduledAtUtc);
    }

    [Fact]
    public void FixedDelay_DiffersFromInterval_WhenExecutionIsSlow()
    {
        // Same 5-minute setting, execution took 4 minutes (10:00 → 10:04):
        // interval fires at 10:05, fixed delay at 10:09.
        var after = Times.Utc(2026, 8, 1, 10, 0);
        var context = Times.Context(Times.Utc(2026, 8, 1, 10, 4), lastCompletedAtUtc: Times.Utc(2026, 8, 1, 10, 4));

        var interval = new IntervalSchedule(TimeSpan.FromMinutes(5)).GetOccurrenceAfter(after, context)!;
        var fixedDelay = new FixedDelaySchedule(TimeSpan.FromMinutes(5)).GetOccurrenceAfter(after, context)!;

        Assert.Equal(Times.Utc(2026, 8, 1, 10, 5), interval.ScheduledAtUtc);
        Assert.Equal(Times.Utc(2026, 8, 1, 10, 9), fixedDelay.ScheduledAtUtc);
    }
}

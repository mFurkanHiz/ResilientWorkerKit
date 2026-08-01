using ResilientWorkerKit.Scheduling;

namespace ResilientWorkerKit;

/// <summary>Non-generic base used by the registration pipeline.</summary>
public abstract class JobBuilder
{
    private protected JobBuilder(string jobId, Type jobType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        JobId = jobId;
        JobType = jobType;
    }

    internal string JobId { get; }

    internal Type JobType { get; }

    private protected IJobSchedule? Schedule { get; set; }

    private protected bool RunOnStartupFlag { get; set; }

    private protected bool EnabledFlag { get; set; } = true;

    private protected string? DisplayNameValue { get; set; }

    private protected string? TimeZoneId { get; set; }

    private protected TimeSpan? TimeoutValue { get; set; }

    private protected JobRetryOptions RetryOptions { get; } = new();

    private protected OverlapPolicy OverlapPolicyValue { get; set; } = OverlapPolicy.SkipNewExecution;

    private protected MisfirePolicy? MisfirePolicyValue { get; set; }

    private protected TimeSpan? MisfireToleranceValue { get; set; }

    private protected bool DeadLetterFlag { get; set; }

    private protected TimeSpan? IdempotencyTtl { get; set; }

    private protected JobHealthThresholds Health { get; } = new();

    internal JobDefinition Build()
    {
        var timeZone = ResolveTimeZone();
        ValidateRetry();
        ValidateTimeouts();

        var misfire = MisfirePolicyValue ?? DefaultMisfirePolicy();
        ValidateMisfire(misfire);

        return new JobDefinition
        {
            JobId = JobId,
            DisplayName = DisplayNameValue ?? JobId,
            JobType = JobType,
            Enabled = EnabledFlag,
            Schedule = Schedule,
            RunOnStartup = RunOnStartupFlag,
            TimeZone = timeZone,
            Timeout = TimeoutValue,
            Retry = RetryOptions,
            OverlapPolicy = OverlapPolicyValue,
            MisfirePolicy = misfire,
            MisfireTolerance = MisfireToleranceValue,
            DeadLetterOnFailure = DeadLetterFlag,
            IdempotencyTimeToLive = IdempotencyTtl,
            HealthThresholds = Health,
        };
    }

    private TimeZoneInfo ResolveTimeZone()
    {
        if (TimeZoneId is null)
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new JobConfigurationException(
                $"Job '{JobId}': time zone '{TimeZoneId}' was not found on this system. " +
                "Use an IANA id such as 'Europe/Istanbul' or 'UTC'.", ex);
        }
    }

    private MisfirePolicy DefaultMisfirePolicy() => Schedule switch
    {
        FixedDelaySchedule => MisfirePolicy.RescheduleFromNow,
        OneTimeSchedule => MisfirePolicy.RunImmediatelyOnce,
        _ => MisfirePolicy.Skip,
    };

    private void ValidateMisfire(MisfirePolicy policy)
    {
        if (policy == MisfirePolicy.RunIfWithinTolerance && MisfireToleranceValue is not { } tolerance)
        {
            throw new JobConfigurationException(
                $"Job '{JobId}': misfire policy RunIfWithinTolerance requires a tolerance " +
                "(WithMisfirePolicy(MisfirePolicy.RunIfWithinTolerance, tolerance)).");
        }

        if (MisfireToleranceValue is { } t && t <= TimeSpan.Zero)
        {
            throw new JobConfigurationException($"Job '{JobId}': misfire tolerance must be positive.");
        }

        var isCalendar = Schedule is CronSchedule or DailySchedule or WeeklySchedule
            or MonthlySchedule or LastDayOfMonthSchedule or OneTimeSchedule;
        if (policy == MisfirePolicy.RescheduleFromNow && isCalendar)
        {
            throw new JobConfigurationException(
                $"Job '{JobId}': RescheduleFromNow only makes sense for interval/fixed-delay schedules; " +
                "calendar schedules should use Skip, RunImmediatelyOnce or RunIfWithinTolerance.");
        }
    }

    private void ValidateRetry()
    {
        if (RetryOptions.MaxRetries < 0)
        {
            throw new JobConfigurationException($"Job '{JobId}': MaxRetries must be ≥ 0.");
        }

        if (RetryOptions.BaseDelay < TimeSpan.Zero || RetryOptions.MaxDelay < TimeSpan.Zero)
        {
            throw new JobConfigurationException($"Job '{JobId}': retry delays must be ≥ 0.");
        }

        if (RetryOptions.BackoffMultiplier < 1)
        {
            throw new JobConfigurationException($"Job '{JobId}': BackoffMultiplier must be ≥ 1.");
        }

        if (RetryOptions.JitterFactor is < 0 or >= 1)
        {
            throw new JobConfigurationException($"Job '{JobId}': JitterFactor must be in [0, 1).");
        }
    }

    private void ValidateTimeouts()
    {
        if (TimeoutValue is { } total && total <= TimeSpan.Zero)
        {
            throw new JobConfigurationException($"Job '{JobId}': timeout must be positive.");
        }

        if (RetryOptions.AttemptTimeout is { } attempt && attempt <= TimeSpan.Zero)
        {
            throw new JobConfigurationException($"Job '{JobId}': attempt timeout must be positive.");
        }

        if (IdempotencyTtl is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new JobConfigurationException($"Job '{JobId}': idempotency time-to-live must be positive.");
        }
    }

    private protected void SetSchedule(IJobSchedule schedule)
    {
        if (Schedule is not null)
        {
            throw new JobConfigurationException(
                $"Job '{JobId}' already has a schedule ({Schedule.Describe()}). A job takes exactly one schedule.");
        }

        Schedule = schedule;
    }
}

/// <summary>Fluent, strongly-typed configuration for one job registration.</summary>
/// <typeparam name="TJob">The job implementation type.</typeparam>
public sealed class JobBuilder<TJob> : JobBuilder where TJob : class, IWorkerJob
{
    internal JobBuilder(string jobId) : base(jobId, typeof(TJob))
    {
    }

    // ---- Schedules -------------------------------------------------------------------

    /// <summary>Runs at a fixed rate (next = previous scheduled time + interval).</summary>
    public JobBuilder<TJob> WithInterval(TimeSpan interval)
    {
        SetSchedule(new IntervalSchedule(interval));
        return this;
    }

    /// <summary>Runs a fixed delay after each completion (next = previous completion + delay).</summary>
    public JobBuilder<TJob> WithFixedDelay(TimeSpan delay)
    {
        SetSchedule(new FixedDelaySchedule(delay));
        return this;
    }

    /// <summary>Runs on a cron expression (5 fields, or 6 with leading seconds), evaluated in the job's time zone.</summary>
    public JobBuilder<TJob> WithCron(string cronExpression, string? timeZone = null)
    {
        SetSchedule(new CronSchedule(cronExpression));
        return timeZone is null ? this : WithTimeZone(timeZone);
    }

    /// <summary>Runs every day at the given local time.</summary>
    public JobBuilder<TJob> DailyAt(TimeOnly time, string? timeZone = null)
    {
        SetSchedule(new DailySchedule(time));
        return timeZone is null ? this : WithTimeZone(timeZone);
    }

    /// <summary>Runs on the given weekdays at the given local time.</summary>
    public JobBuilder<TJob> WeeklyAt(IEnumerable<DayOfWeek> days, TimeOnly time, string? timeZone = null)
    {
        SetSchedule(new WeeklySchedule(days, time));
        return timeZone is null ? this : WithTimeZone(timeZone);
    }

    /// <summary>Runs once per month on the given day at the given local time.</summary>
    public JobBuilder<TJob> MonthlyOnDay(
        int dayOfMonth,
        TimeOnly time,
        string? timeZone = null,
        MonthlyInvalidDayPolicy invalidDayPolicy = MonthlyInvalidDayPolicy.SkipMonth)
    {
        SetSchedule(new MonthlySchedule(dayOfMonth, time, invalidDayPolicy));
        return timeZone is null ? this : WithTimeZone(timeZone);
    }

    /// <summary>Runs on the actual last day of each month at the given local time.</summary>
    public JobBuilder<TJob> OnLastDayOfMonth(TimeOnly time, string? timeZone = null)
    {
        SetSchedule(new LastDayOfMonthSchedule(time));
        return timeZone is null ? this : WithTimeZone(timeZone);
    }

    /// <summary>Runs exactly once at the given instant.</summary>
    public JobBuilder<TJob> OnceAt(DateTimeOffset runAtUtc)
    {
        SetSchedule(new OneTimeSchedule(runAtUtc));
        return this;
    }

    /// <summary>Uses a custom <see cref="IJobSchedule"/> implementation.</summary>
    public JobBuilder<TJob> WithSchedule(IJobSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        SetSchedule(schedule);
        return this;
    }

    /// <summary>Additionally runs one occurrence immediately when the host starts.</summary>
    public JobBuilder<TJob> RunOnStartup()
    {
        RunOnStartupFlag = true;
        return this;
    }

    // ---- Policies --------------------------------------------------------------------

    /// <summary>Sets the job's time zone (IANA id, e.g. "Europe/Istanbul"). Default UTC.</summary>
    public JobBuilder<TJob> WithTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        TimeZoneId = timeZoneId;
        return this;
    }

    /// <summary>Sets the total execution timeout across all attempts.</summary>
    public JobBuilder<TJob> WithTimeout(TimeSpan timeout)
    {
        TimeoutValue = timeout;
        return this;
    }

    /// <summary>Configures the retry policy for transient failures.</summary>
    public JobBuilder<TJob> WithRetry(Action<JobRetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(RetryOptions);
        return this;
    }

    /// <summary>Shorthand for setting only the maximum retry count.</summary>
    public JobBuilder<TJob> WithRetryCount(int maxRetries)
    {
        RetryOptions.MaxRetries = maxRetries;
        return this;
    }

    /// <summary>
    /// Prevents overlapping executions of this job. Default policy
    /// <see cref="OverlapPolicy.SkipNewExecution"/>: a skipped occurrence cannot pile up work,
    /// and the next regular occurrence runs normally.
    /// </summary>
    public JobBuilder<TJob> PreventOverlappingExecutions(OverlapPolicy policy = OverlapPolicy.SkipNewExecution)
    {
        OverlapPolicyValue = policy;
        return this;
    }

    /// <summary>Allows this job's occurrences to run concurrently (no overlap protection).</summary>
    public JobBuilder<TJob> AllowConcurrentExecutions()
    {
        OverlapPolicyValue = OverlapPolicy.AllowConcurrentExecutions;
        return this;
    }

    /// <summary>Sets the misfire policy (and its tolerance for <see cref="MisfirePolicy.RunIfWithinTolerance"/>).</summary>
    public JobBuilder<TJob> WithMisfirePolicy(MisfirePolicy policy, TimeSpan? tolerance = null)
    {
        MisfirePolicyValue = policy;
        MisfireToleranceValue = tolerance;
        return this;
    }

    /// <summary>
    /// Writes an execution-level dead letter whenever an execution ends as
    /// <see cref="JobExecutionStatus.Failed"/> — both exhausted retries and permanent failures.
    /// Cancelled, timed-out and abandoned executions are not dead-lettered.
    /// </summary>
    public JobBuilder<TJob> DeadLetterOnFailure()
    {
        DeadLetterFlag = true;
        return this;
    }

    /// <summary>Sets a time-to-live for idempotency records created by this job.</summary>
    public JobBuilder<TJob> WithIdempotencyTimeToLive(TimeSpan timeToLive)
    {
        IdempotencyTtl = timeToLive;
        return this;
    }

    /// <summary>Sets the human-readable display name.</summary>
    public JobBuilder<TJob> WithDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayNameValue = displayName;
        return this;
    }

    /// <summary>Registers the job but keeps it disabled (not scheduled, not manually triggerable).</summary>
    public JobBuilder<TJob> Disabled()
    {
        EnabledFlag = false;
        return this;
    }

    /// <summary>Configures per-job health thresholds.</summary>
    public JobBuilder<TJob> WithHealthThresholds(Action<JobHealthThresholds> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Health);
        return this;
    }
}

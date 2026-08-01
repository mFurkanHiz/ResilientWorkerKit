namespace ResilientWorkerKit;

/// <summary>
/// Behavior of a monthly schedule when the configured day does not exist in a month
/// (e.g. day 31 in April, day 29–31 in February).
/// </summary>
public enum MonthlyInvalidDayPolicy
{
    /// <summary>The job does not run at all in months lacking the configured day.</summary>
    SkipMonth = 0,

    /// <summary>The job runs on the last day of months lacking the configured day.</summary>
    RunOnLastAvailableDay = 1,

    /// <summary>
    /// A day that cannot exist in every month (29–31) is rejected at startup with a
    /// <see cref="JobConfigurationException"/>, forcing an explicit choice.
    /// </summary>
    FailConfiguration = 2,
}

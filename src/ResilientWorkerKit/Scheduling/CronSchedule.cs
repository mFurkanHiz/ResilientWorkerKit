using System.Globalization;
using Cronos;

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Standard cron schedule evaluated in the job's time zone (DST handled by the Cronos
/// library). Supports 5-field expressions (<c>minute hour day month day-of-week</c>) and
/// 6-field expressions with a leading seconds field. See docs/scheduling.md for the
/// supported syntax.
/// </summary>
public sealed class CronSchedule : IJobSchedule
{
    private readonly CronExpression _expression;
    private readonly string _expressionText;

    /// <summary>Parses the cron expression.</summary>
    /// <exception cref="JobConfigurationException">The expression is invalid.</exception>
    public CronSchedule(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        _expressionText = expression.Trim();
        var fieldCount = _expressionText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        try
        {
            _expression = CronExpression.Parse(
                _expressionText,
                fieldCount == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            throw new JobConfigurationException($"Invalid cron expression '{expression}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public JobScheduleOccurrence? GetOccurrenceAfter(DateTimeOffset afterUtc, ScheduleCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var next = _expression.GetNextOccurrence(afterUtc, context.TimeZone, inclusive: false);
        if (next is not { } scheduledUtc)
        {
            return null;
        }

        return new JobScheduleOccurrence(
            scheduledUtc,
            LocalTimeConverter.ToLocal(scheduledUtc, context.TimeZone),
            scheduledUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public string Describe() => $"cron '{_expressionText}'";
}

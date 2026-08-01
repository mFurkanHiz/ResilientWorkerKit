namespace ResilientWorkerKit.UnitTests.TestInfrastructure;

internal static class Times
{
    public static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    public static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    public static ScheduleCalculationContext Context(
        DateTimeOffset nowUtc,
        string timeZone = "UTC",
        DateTimeOffset? lastCompletedAtUtc = null)
        => new(nowUtc, lastCompletedAtUtc, Zone(timeZone));
}

namespace ResilientWorkerKit.Scheduling;

/// <summary>
/// Converts local wall-clock times to UTC with well-defined daylight-saving behavior:
/// invalid local times (spring-forward gap) are shifted forward to the end of the gap;
/// ambiguous local times (fall-back hour) resolve to their first (earlier UTC) occurrence,
/// so a schedule never fires twice for one wall-clock time.
/// </summary>
internal static class LocalTimeConverter
{
    /// <summary>Converts a local date-time in the given zone to UTC applying the DST policy above.</summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(local))
        {
            // Walk forward in 15-minute steps until the gap ends (DST gaps are 30–120 minutes).
            var probe = local;
            for (var i = 0; i < 12 && timeZone.IsInvalidTime(probe); i++)
            {
                probe = probe.AddMinutes(15);
            }

            local = probe;
        }

        if (timeZone.IsAmbiguousTime(local))
        {
            // First occurrence = the larger UTC offset (the pre-transition, DST offset).
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);
            var first = offsets.Max();
            return new DateTimeOffset(local, first).ToUniversalTime();
        }

        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Converts a UTC instant to the zone's local wall-clock time.</summary>
    public static DateTime ToLocal(DateTimeOffset utc, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTime(utc, timeZone).DateTime;
}

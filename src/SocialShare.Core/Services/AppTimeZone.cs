namespace SocialShare.Core.Services;

/// <summary>
/// Everything is stored in UTC. This converts at the edges using the IANA id on the profile.
/// .NET resolves IANA ids on every platform we care about, so no conversion table is needed.
/// </summary>
public static class AppTimeZone
{
    public const string Default = "America/Detroit";

    public static TimeZoneInfo Resolve(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId))
        {
            ianaId = Default;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static DateTime ToLocal(DateTimeOffset utc, string? ianaId) =>
        TimeZoneInfo.ConvertTime(utc, Resolve(ianaId)).DateTime;

    /// <summary>Turns a wall clock value the user picked into the UTC instant we store.</summary>
    public static DateTimeOffset ToUtc(DateTime local, string? ianaId)
    {
        var zone = Resolve(ianaId);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // A time that does not exist because the clocks jumped forward is pushed past the gap.
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        var offset = zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    /// <summary>The zone list offered in settings. Kept short and practical rather than exhaustive.</summary>
    public static IReadOnlyList<string> CommonZones { get; } =
    [
        "America/Detroit",
        "America/New_York",
        "America/Chicago",
        "America/Denver",
        "America/Phoenix",
        "America/Los_Angeles",
        "America/Anchorage",
        "Pacific/Honolulu",
        "America/Toronto",
        "America/Sao_Paulo",
        "Europe/London",
        "Europe/Dublin",
        "Europe/Lisbon",
        "Europe/Paris",
        "Europe/Berlin",
        "Europe/Madrid",
        "Europe/Rome",
        "Europe/Amsterdam",
        "Europe/Stockholm",
        "Europe/Warsaw",
        "Europe/Athens",
        "Europe/Kyiv",
        "Africa/Lagos",
        "Africa/Johannesburg",
        "Africa/Nairobi",
        "Asia/Jerusalem",
        "Asia/Dubai",
        "Asia/Karachi",
        "Asia/Kolkata",
        "Asia/Bangkok",
        "Asia/Singapore",
        "Asia/Shanghai",
        "Asia/Hong_Kong",
        "Asia/Tokyo",
        "Asia/Seoul",
        "Australia/Perth",
        "Australia/Brisbane",
        "Australia/Sydney",
        "Pacific/Auckland",
        "UTC"
    ];
}

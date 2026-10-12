namespace Vuelto.Shared.Ui.Components;

/// <summary>
/// The inbox "Import from" field is a calendar day in the user's own time zone, while the API stores an
/// instant (<c>import_from</c> is the polling cursor). The day the user picks must be the first day
/// whose mail is fetched, and reading it back must show the same day — so the wire value is that day's
/// local midnight with the device's UTC offset, and the display is the instant converted back to local
/// time. (Sending midnight UTC, as before, was read back in Costa Rica as 6 PM of the previous day: the
/// form showed one day earlier than the user had set, and re-saving "the same" day sent nothing.)
/// </summary>
public static class ImportFromDate
{
    /// <summary>The instant to send for a picked day: that day's 00:00 in <paramref name="zone"/>.</summary>
    public static DateTimeOffset ToWire(DateOnly day, TimeZoneInfo zone)
    {
        var localMidnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(localMidnight, zone.GetUtcOffset(localMidnight));
    }

    /// <summary>The day to show for a stored instant: its date in <paramref name="zone"/>.</summary>
    public static DateOnly FromWire(DateTimeOffset stored, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stored, zone).DateTime);
}

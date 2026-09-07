using Vuelto.Shared.Ui.Components;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// EMAIL-2 "Import from": the day the user picks is the first day fetched and reads back as the same day,
/// whatever the device's time zone (Costa Rica is UTC−6 all year; the owner's bug report).
/// </summary>
public class ImportFromDateTests
{
    private static readonly TimeZoneInfo CostaRica = TimeZoneInfo.CreateCustomTimeZone("CR", TimeSpan.FromHours(-6), "Costa Rica", "Costa Rica");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void ToWire_IsLocalMidnight_WithTheZonesOffset()
    {
        var wire = ImportFromDate.ToWire(new DateOnly(2026, 9, 5), CostaRica);

        Assert.Equal(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.FromHours(-6)), wire);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 6, 0, 0, TimeSpan.Zero), wire); // the same instant, seen from UTC
    }

    [Fact]
    public void FromWire_ShowsTheDayThatWasPicked_RoundTrip()
    {
        var day = new DateOnly(2026, 9, 5);

        Assert.Equal(day, ImportFromDate.FromWire(ImportFromDate.ToWire(day, CostaRica), CostaRica));
        Assert.Equal(day, ImportFromDate.FromWire(ImportFromDate.ToWire(day, Utc), Utc));
    }

    [Fact]
    public void FromWire_LegacyMidnightUtc_ShowsTheEveningBeforeInCostaRica()
    {
        // Rows saved before the fix hold 00:00Z = 18:00 the previous day in Costa Rica — and that IS where their
        // polling window starts, so showing that day is truthful (re-saving the intended day fixes the row).
        var legacy = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 9, 4), ImportFromDate.FromWire(legacy, CostaRica));
        Assert.Equal(new DateOnly(2026, 9, 5), ImportFromDate.FromWire(legacy, Utc));
    }
}

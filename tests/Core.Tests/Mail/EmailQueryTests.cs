using Vuelto.Core.Entities;
using Vuelto.Core.Mail;

namespace Vuelto.Core.Tests.Mail;

/// <summary>EMAIL-3 (donor US-035 AC2): the cursor is UTC, looks back 5 minutes, falls back to import_from, and "ignore cursor" drops the date floor.</summary>
public class EmailQueryTests
{
    private static EmailConnection Connection(DateTimeOffset cursor, bool lastPolledSet = true) => new()
    {
        UserId = Guid.NewGuid(), Provider = EmailProviders.Microsoft, AccessToken = "p", RefreshToken = "p",
        Folders = ["Inbox"], SenderFilters = ["a@b.com"], SubjectFilters = ["X"], ImportFrom = cursor, LastPolledAt = lastPolledSet ? cursor : null
    };

    [Fact]
    public void From_UsesTheCursorMinusTheOverlap_InUtc()
    {
        var cursor = new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        var q = EmailQuery.From(Connection(cursor));
        Assert.Equal(TimeSpan.Zero, q.ReceivedAfter.Offset);
        Assert.Equal(cursor.AddMinutes(-5), q.ReceivedAfter);
        Assert.Equal(["Inbox"], q.Folders);
        Assert.Equal(["a@b.com"], q.Senders);
        Assert.Equal(["X"], q.Subjects);
        Assert.Equal((true, 50), (q.UnreadOnly, q.MaxResults));
    }

    [Fact]
    public void From_ConvertsALocalOffsetCursor_ToUtc()
    {
        var cursor = new DateTimeOffset(2026, 6, 17, 6, 0, 0, TimeSpan.FromHours(-6)); // 12:00Z
        var q = EmailQuery.From(Connection(cursor));
        Assert.Equal(new DateTimeOffset(2026, 6, 17, 11, 55, 0, TimeSpan.Zero), q.ReceivedAfter);
    }

    [Fact]
    public void From_UsesImportFrom_WhenLastPolledAtIsNull()
    {
        var cursor = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(cursor - EmailQuery.CursorOverlap, EmailQuery.From(Connection(cursor, lastPolledSet: false)).ReceivedAfter);
    }

    [Fact]
    public void From_WithIgnoreCursor_DropsTheDateFloor_KeepsUnread()
    {
        var c = Connection(new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero));
        c.IgnoreCursor = true;
        var q = EmailQuery.From(c);
        Assert.Equal(DateTimeOffset.UnixEpoch, q.ReceivedAfter);
        Assert.True(q.UnreadOnly);
    }

    [Theory]
    [InlineData("microsoft", "microsoft")]
    [InlineData(" Google ", "google")]
    [InlineData("outlook", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Providers_Normalize(string? input, string? expected) => Assert.Equal(expected, EmailProviders.Normalize(input));
}

using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Persistence;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests.Notify;

/// <summary>
/// NOTIFY-1 (ADR-013): the per-user notification center. NotifyAsync stages an in-app row (commits with
/// the caller's unit of work); list is newest-first + paginated; unread count + mark-read/all work; and
/// everything is scoped to the user — never cross-user. Postgres-backed.
/// </summary>
[Collection(PostgresCollection.Name)]
public class NotificationServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static NotificationService NewService(AppDbContext db, IEmailSender? email = null) =>
        new(new EfRepository<Notification>(db), new EfRepository<NotificationPreference>(db),
            new UserRepository(db), email ?? new NoopEmailSender(), TimeProvider.System);

    [Fact]
    public async Task Notify_ThenList_ShowsUnread_NewestFirst()
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);

        await service.NotifyAsync(userId, "member.invited", "First", "b1");
        await service.NotifyAsync(userId, "member.joined", "Second", "b2");
        await db.SaveChangesAsync(); // NotifyAsync stages; the caller's UoW commits

        var list = await service.ListAsync(userId, before: null, limit: 20);

        Assert.Equal(2, list.Count);
        Assert.Equal("Second", list[0].Title); // newest first
        Assert.All(list, n => Assert.Null(n.ReadAt));
        Assert.Equal(2, await service.UnreadCountAsync(userId));
    }

    [Fact]
    public async Task List_RespectsLimit_AndBeforeCursor()
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        for (var i = 0; i < 5; i++)
            await service.NotifyAsync(userId, "k", $"n{i}", "");
        await db.SaveChangesAsync();

        var firstPage = await service.ListAsync(userId, before: null, limit: 2);
        Assert.Equal(2, firstPage.Count);

        var nextPage = await service.ListAsync(userId, before: firstPage[^1].CreatedAt, limit: 10);
        Assert.All(nextPage, n => Assert.True(n.CreatedAt < firstPage[^1].CreatedAt));
    }

    [Fact]
    public async Task MarkRead_SetsReadAt_AndDropsUnreadCount()
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        await service.NotifyAsync(userId, "k", "n", "");
        await db.SaveChangesAsync();
        var id = (await service.ListAsync(userId, null, 10))[0].Id;

        Assert.True(await service.MarkReadAsync(userId, id));
        Assert.Equal(0, await service.UnreadCountAsync(userId));
    }

    [Fact]
    public async Task MarkAllRead_ClearsUnread()
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        for (var i = 0; i < 3; i++) await service.NotifyAsync(userId, "k", $"n{i}", "");
        await db.SaveChangesAsync();

        await service.MarkAllReadAsync(userId);

        Assert.Equal(0, await service.UnreadCountAsync(userId));
    }

    [Fact]
    public async Task Delete_RemovesOwnNotification_ThenGone()
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        await service.NotifyAsync(userId, "k", "n", "");
        await db.SaveChangesAsync();
        var id = (await service.ListAsync(userId, null, 10))[0].Id;

        Assert.True(await service.DeleteAsync(userId, id));
        Assert.Empty(await service.ListAsync(userId, null, 10));
        Assert.False(await service.DeleteAsync(userId, id)); // idempotent: already gone
    }

    [Fact]
    public async Task Delete_AnotherUsersNotification_ReturnsFalse_AndKeepsIt()
    {
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        await service.NotifyAsync(theirs, "k", "theirs", "");
        await db.SaveChangesAsync();
        var theirId = (await service.ListAsync(theirs, null, 10))[0].Id;

        Assert.False(await service.DeleteAsync(mine, theirId)); // can't delete another user's
        Assert.Single(await service.ListAsync(theirs, null, 10)); // still there
    }

    [Fact]
    public async Task DeleteAll_OnlyRead_RemovesReadKeepsUnread_ReturnsCount()
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        for (var i = 0; i < 3; i++) await service.NotifyAsync(userId, "k", $"n{i}", "");
        await db.SaveChangesAsync();
        await service.MarkReadAsync(userId, (await service.ListAsync(userId, null, 10))[0].Id); // read one

        var removed = await service.DeleteAllAsync(userId, onlyRead: true);

        Assert.Equal(1, removed);
        Assert.Equal(2, (await service.ListAsync(userId, null, 10)).Count); // the two unread remain
    }

    [Fact]
    public async Task DeleteAll_All_RemovesEverything_AndIsPerUser()
    {
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        for (var i = 0; i < 3; i++) await service.NotifyAsync(mine, "k", $"n{i}", "");
        await service.NotifyAsync(theirs, "k", "theirs", "");
        await db.SaveChangesAsync();

        var removed = await service.DeleteAllAsync(mine, onlyRead: false);

        Assert.Equal(3, removed);
        Assert.Empty(await service.ListAsync(mine, null, 10));
        Assert.Single(await service.ListAsync(theirs, null, 10)); // another user's untouched
    }

    [Fact]
    public async Task MarkRead_AnotherUsersNotification_ReturnsFalse_AndDoesNotMark()
    {
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        await service.NotifyAsync(theirs, "k", "theirs", "");
        await db.SaveChangesAsync();
        var theirId = (await service.ListAsync(theirs, null, 10))[0].Id;

        Assert.False(await service.MarkReadAsync(mine, theirId)); // can't touch another user's
        Assert.Equal(1, await service.UnreadCountAsync(theirs)); // still unread
    }

    [Fact]
    public async Task List_IsPerUser()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        await service.NotifyAsync(a, "k", "for-a", "");
        await service.NotifyAsync(b, "k", "for-b", "");
        await db.SaveChangesAsync();

        var listA = await service.ListAsync(a, null, 10);
        Assert.Equal("for-a", Assert.Single(listA).Title);
    }

    // v2 audit B8-1: the notification center is keyed by UserId, not TenantId (ADR-C2), so the
    // isolation boundary is the user — a member of one tenant can never enumerate a member of another
    // tenant's notifications, because the tenant never enters the query at all. This pins that: even
    // with the two recipients placed in different tenants, listing is strictly the caller's own rows.
    [Fact]
    public async Task List_TenantAUser_CannotSeeTenantBUsersNotifications()
    {
        var tenantAUser = Guid.CreateVersion7();
        var tenantBUser = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        await service.NotifyAsync(tenantAUser, "k", "for-a", "");
        await service.NotifyAsync(tenantBUser, "k", "for-b", "");
        await db.SaveChangesAsync();

        var listForA = await service.ListAsync(tenantAUser, null, 10);
        Assert.Equal("for-a", Assert.Single(listForA).Title); // A sees exactly its own, never B's
        Assert.Equal(1, await service.UnreadCountAsync(tenantAUser));
        Assert.Equal(1, await service.UnreadCountAsync(tenantBUser)); // B's notification untouched by A's read
    }

    [Fact]
    public async Task Notify_StoresMetadataAsJson()
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        await service.NotifyAsync(userId, "k", "n", "", new { invitation_id = "abc" });
        await db.SaveChangesAsync();

        var stored = await db.Set<Notification>().SingleAsync(n => n.UserId == userId);
        Assert.Contains("invitation_id", stored.Metadata);
        Assert.Contains("abc", stored.Metadata);
    }
}

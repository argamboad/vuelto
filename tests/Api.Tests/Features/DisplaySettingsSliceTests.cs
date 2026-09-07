using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.DisplaySettings;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// DISPLAY-1 on real Postgres: a read never writes (both + is_default until the user chooses), the upsert
/// normalises and validates, rows are per user (one user's choice never shows for another), and the
/// account-erasure contributor removes exactly the user's row.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DisplaySettingsSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ctx(AppDbContext Db, DisplaySettingsHandler Handler, Guid UserId, Guid OtherUserId);

    private async Task<Ctx> ContextAsync()
    {
        var db = Fixture.CreateContext(Guid.CreateVersion7());
        var user = new User { Email = $"{Guid.NewGuid():N}@test.local", CreatedAt = T0, UpdatedAt = T0 };
        var other = new User { Email = $"{Guid.NewGuid():N}@test.local", CreatedAt = T0, UpdatedAt = T0 };
        db.AddRange(user, other);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Ctx(db, new DisplaySettingsHandler(new EfRepository<UserDisplaySettings>(db), new FakeTimeProvider(T0)), user.Id, other.Id);
    }

    [Fact]
    public async Task Read_NeverWrites_AndDefaultsToBoth()
    {
        var c = await ContextAsync();

        var first = await c.Handler.GetAsync(c.UserId, default);

        Assert.Equal(("both", true), (first.DisplayCurrency, first.IsDefault));
        Assert.Equal(0, await c.Db.UserDisplaySettings.CountAsync());
    }

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData(" Crc ", "CRC")]
    [InlineData("BOTH", "both")]
    public async Task Update_NormalisesAndUpserts_ThenReadsBack(string sent, string stored)
    {
        var c = await ContextAsync();

        var (saved, error) = await c.Handler.UpdateAsync(c.UserId, new UpdateDisplaySettingsRequest(sent), default);
        var (again, _) = await c.Handler.UpdateAsync(c.UserId, new UpdateDisplaySettingsRequest(sent), default); // idempotent: still one row

        Assert.Null(error);
        Assert.Equal((stored, false), (saved!.DisplayCurrency, saved.IsDefault));
        Assert.Equal(stored, again!.DisplayCurrency);
        var row = await c.Db.UserDisplaySettings.SingleAsync();
        Assert.Equal((c.UserId, stored, T0), (row.UserId, row.DisplayCurrency, row.UpdatedAt));
        Assert.Equal((stored, false), ((await c.Handler.GetAsync(c.UserId, default)).DisplayCurrency, (await c.Handler.GetAsync(c.UserId, default)).IsDefault));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EUR")]
    public async Task Update_RejectsAnythingButTheThree_AndWritesNothing(string? value)
    {
        var c = await ContextAsync();

        var (saved, error) = await c.Handler.UpdateAsync(c.UserId, new UpdateDisplaySettingsRequest(value), default);

        Assert.Null(saved);
        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains("display_currency", error.Message);
        Assert.Equal(0, await c.Db.UserDisplaySettings.CountAsync());
    }

    [Fact]
    public async Task Rows_ArePerUser_AndErasureRemovesOnlyTheUsers()
    {
        var c = await ContextAsync();
        await c.Handler.UpdateAsync(c.UserId, new UpdateDisplaySettingsRequest("USD"), default);
        await c.Handler.UpdateAsync(c.OtherUserId, new UpdateDisplaySettingsRequest("CRC"), default);

        Assert.Equal("USD", (await c.Handler.GetAsync(c.UserId, default)).DisplayCurrency);
        Assert.Equal("CRC", (await c.Handler.GetAsync(c.OtherUserId, default)).DisplayCurrency);

        await new DisplaySettingsUserDataContributor(new EfRepository<UserDisplaySettings>(c.Db)).WipeAsync(c.UserId);

        c.Db.ChangeTracker.Clear();
        var left = await c.Db.UserDisplaySettings.SingleAsync();
        Assert.Equal((c.OtherUserId, "CRC"), (left.UserId, left.DisplayCurrency));
        Assert.True((await c.Handler.GetAsync(c.UserId, default)).IsDefault);
    }
}

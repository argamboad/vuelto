using Microsoft.Extensions.Options;
using Vuelto.Api.Configuration;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Admin;

/// <summary>
/// ADMIN-1 (ADR-014): platform-staff membership is an out-of-band config email allowlist, checked
/// case-insensitively; unknown users and an empty allowlist are not staff (fail closed).
/// </summary>
[Collection(PostgresCollection.Name)]
public class PlatformStaffServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task AllowlistedEmail_IsStaff_CaseInsensitive()
    {
        await using var db = Fixture.CreateContext();
        var userId = await SeedUserAsync(db, "boss@corp.com");
        var service = NewService(db, "BOSS@corp.com"); // different case

        Assert.True(await service.IsStaffAsync(userId));
    }

    [Fact]
    public async Task NonAllowlistedEmail_IsNotStaff()
    {
        await using var db = Fixture.CreateContext();
        var userId = await SeedUserAsync(db, "nobody@corp.com");
        Assert.False(await NewService(db, "boss@corp.com").IsStaffAsync(userId));
    }

    [Fact]
    public async Task UnknownUser_IsNotStaff()
    {
        await using var db = Fixture.CreateContext();
        Assert.False(await NewService(db, "boss@corp.com").IsStaffAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task EmptyAllowlist_NoOneIsStaff()
    {
        await using var db = Fixture.CreateContext();
        var userId = await SeedUserAsync(db, "boss@corp.com");
        Assert.False(await NewService(db).IsStaffAsync(userId));
    }

    private static PlatformStaffService NewService(AppDbContext db, params string[] staffEmails) =>
        new(new UserRepository(db), Options.Create(new PlatformAdminSettings { StaffEmails = staffEmails }));

    private static async Task<Guid> SeedUserAsync(AppDbContext db, string email)
    {
        var user = new User { Id = Guid.CreateVersion7(), Email = email };
        db.Set<User>().Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}

using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Vuelto.Api.Controllers;
using Vuelto.Api.Models;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Audit;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Gdpr;

/// <summary>
/// GDPR-1 (ADR-011): a tenant data export assembles core (tenant/members/invitations) + every
/// contributor's section into a JSON bundle written via <see cref="IFileStorage"/> and handed back as a
/// signed URL — owner-only, tenant-scoped, secret-free, audited. Uses a capturing file storage to
/// inspect the exact bytes; Postgres for the real reads + audit.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantExportTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private const string InvitationTokenHash = "SUPER-SECRET-TOKEN-HASH";

    [Fact]
    public async Task Export_AssemblesBundle_StoresJson_ReturnsSignedUrl_AndAudits()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        await SeedMembershipAsync(tenantId, TenantRoles.Member);
        await SeedWidgetAsync(tenantId, "My Note / note body text");
        await SeedInvitationAsync(tenantId, "invitee@x.com");
        await SeedAuditAsync(tenantId, "test.seeded");

        await using var db = Fixture.CreateContext(tenantId);
        var storage = new CapturingFileStorage();
        var result = await NewExportService(db, storage).ExportAsync(tenantId, ownerId);

        // Returned URL is what the storage handed back; the artifact is JSON under an exports/ key.
        Assert.Equal(storage.Url, result);
        Assert.Equal("application/json", storage.ContentType);
        Assert.StartsWith("exports/", storage.Key);

        var json = Encoding.UTF8.GetString(storage.Content!);
        Assert.Contains("\"members\"", json);
        Assert.Contains("invitee@x.com", json);      // invitation email present
        Assert.Contains("My Note", json);            // contributor section (fixture widget)
        Assert.Contains("note body text", json);
        Assert.Contains("test.seeded", json);        // audit section (contributor)
        Assert.DoesNotContain(InvitationTokenHash, json); // secret NEVER exported

        // The export itself is audited.
        await using var read = Fixture.CreateContext(tenantId);
        var events = read.Set<AuditEvent>().Where(e => e.Action == "tenant.exported").ToList();
        var exportEvent = Assert.Single(events);
        Assert.Equal(ownerId, exportEvent.ActorUserId);
    }

    [Fact]
    public async Task Export_OnlyIncludesOwnTenant()
    {
        var mine = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(mine, TenantRoles.Owner);
        await SeedWidgetAsync(mine, "mine-only");

        var other = Guid.CreateVersion7();
        await SeedMembershipAsync(other, TenantRoles.Owner);
        await SeedWidgetAsync(other, "OTHER-TENANT-SECRET");

        await using var db = Fixture.CreateContext(mine);
        var storage = new CapturingFileStorage();
        await NewExportService(db, storage).ExportAsync(mine, ownerId);

        var json = Encoding.UTF8.GetString(storage.Content!);
        Assert.Contains("mine-only", json);
        Assert.DoesNotContain("OTHER-TENANT-SECRET", json);
    }

    [Fact]
    public async Task Owner_ExportEndpoint_ReturnsDownloadUrl()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);

        await using var db = Fixture.CreateContext(tenantId);
        var storage = new CapturingFileStorage();
        var controller = NewController(db, NewExportService(db, storage), ownerId);

        var result = await controller.Export(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<TenantExportResponse>(ok.Value);
        Assert.Equal(storage.Url.ToString(), body.DownloadUrl);
    }

    [Theory]
    [InlineData(TenantRoles.Member)]
    [InlineData(TenantRoles.Admin)]
    public async Task NonOwner_ExportEndpoint_Returns403_WithoutExporting(string role)
    {
        var tenantId = Guid.CreateVersion7();
        await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var callerId = await SeedMembershipAsync(tenantId, role);

        await using var db = Fixture.CreateContext(tenantId);

        // ExportData gate moved to [RequireTenantPermission] (B9-5); run it as the pipeline would.
        var result = await TenantPermissionGate.RunAsync(
            typeof(HouseholdController), nameof(HouseholdController.Export), new TenantRepository(db), callerId, tenantId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // --- construction ---

    private static TenantExportService NewExportService(AppDbContext db, IFileStorage storage)
    {
        var tenants = new TenantRepository(db);
        var harness = new ServiceHarness(db);
        var contributors = new ITenantDataContributor[]
        {
            new TestWidgetDataContributor(new EfRepository<TestWidget>(db)),
            new AuditDataContributor(new EfRepository<AuditEvent>(db)),
        };
        return new TenantExportService(
            tenants,
            harness.InvitationService(),
            contributors,
            storage,
            new AuditLog(new EfRepository<AuditEvent>(db), TimeProvider.System),
            new EfRepository<AuditEvent>(db),
            TimeProvider.System);
    }

    private HouseholdController NewController(AppDbContext db, ITenantExportService export, Guid currentUserId)
    {
        var controller = new HouseholdController(
            new ServiceHarness(db).TenantService(), new TenantRepository(db), export);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString())], authenticationType: "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    // --- seeding ---

    private async Task<Guid> SeedMembershipAsync(Guid tenantId, string role)
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        if (!await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Set<Tenant>(), t => t.Id == tenantId))
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Tenant" });
        db.Set<User>().Add(new User { Id = userId, Email = $"u-{userId:N}@x.com" });
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenantId, UserId = userId, Role = role });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task SeedWidgetAsync(Guid tenantId, string name)
    {
        await using var db = Fixture.CreateContext(tenantId); // interceptor stamps TenantId
        db.Set<TestWidget>().Add(new TestWidget { Name = name, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task SeedInvitationAsync(Guid tenantId, string email)
    {
        await using var db = Fixture.CreateContext(tenantId);
        db.Set<TenantInvitation>().Add(new TenantInvitation
        {
            InvitedEmail = email,
            InvitedByUserId = Guid.CreateVersion7(),
            Status = InvitationStatuses.Pending,
            TokenHash = InvitationTokenHash,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAuditAsync(Guid tenantId, string action)
    {
        await using var db = Fixture.CreateContext(tenantId);
        await new AuditLog(new EfRepository<AuditEvent>(db), TimeProvider.System).RecordAsync(action);
        await db.SaveChangesAsync();
    }
}

/// <summary>Captures the bytes written so a test can inspect the export bundle without real storage.</summary>
internal sealed class CapturingFileStorage : IFileStorage
{
    public string? Key { get; private set; }
    public byte[]? Content { get; private set; }
    public string? ContentType { get; private set; }
    public Uri Url { get; } = new("https://files.test/download/signed");

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        Key = key;
        ContentType = contentType;
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);
        Content = ms.ToArray();
    }

    public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        Task.FromResult(Url);

    public Task<FileObject?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

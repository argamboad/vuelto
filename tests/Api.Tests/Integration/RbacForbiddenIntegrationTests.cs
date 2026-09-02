using System.Net;
using System.Net.Http.Json;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// v2 audit B9-5 (ADR-009 RBAC): locks the tenant-permission 403 behavior at the HTTP boundary so the
/// planned move to a single <c>[RequireTenantPermission]</c> filter can't drift it. Per the role matrix,
/// rename (RenameTenant) + invite (ManageMembers) are owner-or-admin, so only a plain member is
/// forbidden. A denied caller gets 403 with the standard error envelope; the token is valid, never 401.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RbacForbiddenIntegrationTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task RenameHousehold_AsMember_Returns403()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var resp = await _factory.CreateClientFor(member)
            .PutAsJsonAsync("/api/household", new { name = "Renamed" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        await AssertErrorEnvelopeAsync(resp, "forbidden");
    }

    [Theory]
    [InlineData(TenantRoles.Owner)]
    [InlineData(TenantRoles.Admin)] // admin has RenameTenant in the matrix
    public async Task RenameHousehold_AsOwnerOrAdmin_IsNotForbidden(string role)
    {
        var user = await _factory.SeedUserAsync(role);
        var resp = await _factory.CreateClientFor(user)
            .PutAsJsonAsync("/api/household", new { name = "Renamed" });

        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode); // permission granted
    }

    [Fact]
    public async Task InviteMember_AsMember_Returns403()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var resp = await _factory.CreateClientFor(member)
            .PostAsJsonAsync("/api/household/invitations", new { email = "invitee@test.local" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        await AssertErrorEnvelopeAsync(resp, "forbidden");
    }

    [Fact]
    public async Task InviteMember_AsAdmin_IsNotForbidden()
    {
        // ManageMembers is owner-or-admin — admin must NOT be forbidden. Assert not-403 (the RBAC
        // guarantee) rather than a specific success code, which depends on quota/validation.
        var admin = await _factory.SeedUserAsync(TenantRoles.Admin);
        var resp = await _factory.CreateClientFor(admin)
            .PostAsJsonAsync("/api/household/invitations", new { email = "invitee@test.local" });

        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private static async Task AssertErrorEnvelopeAsync(HttpResponseMessage resp, string expectedError)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(expectedError, doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.TryGetProperty("message", out _), "error envelope must carry a message");
    }
}

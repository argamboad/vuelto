using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Entities;

namespace Perezosoft.Api.Tests.Integration;

/// <summary>
/// v2 audit B8-6: proves the integration harness boots the real app and that the end-to-end pipeline
/// (JWT-bearer auth → tenant filter → controller → Postgres) actually enforces what the unit tests
/// assert in isolation. In particular tenant scoping is proven at the WIRE: two owners in two tenants
/// hit the same route and each sees only their own household. This is the keystone the MFA-integration
/// (B8-2) and controller-split (B9-1) work builds on.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class HarnessSmokeTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/notifications");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidToken_Returns200()
    {
        var user = await _factory.SeedUserAsync();
        var client = _factory.CreateClientFor(user);

        var response = await client.GetAsync("/api/notifications");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Household_ReturnsCallersOwnTenant_WithRole()
    {
        var owner = await _factory.SeedUserAsync(TenantRoles.Owner);
        var client = _factory.CreateClientFor(owner);

        var body = await client.GetFromJsonAsync<HouseholdDto>("/api/household");

        Assert.NotNull(body);
        Assert.Equal(owner.TenantId, body!.Id);
        Assert.Equal(TenantRoles.Owner, body.MyRole);
        Assert.Contains(body.Members, m => m.UserId == owner.UserId);
    }

    [Fact]
    public async Task Household_IsTenantScoped_AcrossTheRealHttpStack()
    {
        // Two owners, two tenants. Each calls the identical route with their own token; the JWT tenant_id
        // claim drives the global filter, so neither can see the other's household or members.
        var a = await _factory.SeedUserAsync(TenantRoles.Owner);
        var b = await _factory.SeedUserAsync(TenantRoles.Owner);

        var bodyA = await _factory.CreateClientFor(a).GetFromJsonAsync<HouseholdDto>("/api/household");
        var bodyB = await _factory.CreateClientFor(b).GetFromJsonAsync<HouseholdDto>("/api/household");

        Assert.Equal(a.TenantId, bodyA!.Id);
        Assert.Equal(b.TenantId, bodyB!.Id);
        Assert.DoesNotContain(bodyA.Members, m => m.UserId == b.UserId); // A never sees B's member
        Assert.DoesNotContain(bodyB.Members, m => m.UserId == a.UserId); // and vice-versa
    }

    // Minimal projections of the TenantResponse JSON (snake_case, per the API's JsonPropertyName) —
    // kept local so the harness proof doesn't couple to the controller's DTO type.
    private sealed record HouseholdDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("my_role")] string MyRole,
        [property: JsonPropertyName("members")] IReadOnlyList<MemberDto> Members);

    private sealed record MemberDto(
        [property: JsonPropertyName("user_id")] Guid UserId,
        [property: JsonPropertyName("role")] string Role);
}

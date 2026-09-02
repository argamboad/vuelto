using System.Net;
using System.Net.Http.Json;
using Perezosoft.Api.Tests.Infrastructure;

namespace Perezosoft.Api.Tests.Integration;

/// <summary>
/// v3 audit LB-UI-10 (T42): the bulk clear must never DEFAULT to the widest blast radius. Before, an
/// omitted <c>?read</c> bound to <c>false</c> and silently deleted ALL of the caller's notifications — one
/// dropped query param away from wiping everything. The widest scope (delete all) must now be explicit
/// (<c>?read=false</c>); omitting the scope is a 400.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class NotificationClearScopeTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Clear_WithoutScope_Is400_NotDeleteAll()
    {
        var user = await _factory.SeedUserAsync();

        var resp = await _factory.CreateClientFor(user).DeleteAsync("/api/notifications");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("scope_required", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/notifications?read=true")]  // clear only the read ones
    [InlineData("/api/notifications?read=false")] // clear all — but explicitly
    public async Task Clear_WithExplicitScope_Succeeds(string url)
    {
        var user = await _factory.SeedUserAsync();

        var resp = await _factory.CreateClientFor(user).DeleteAsync(url);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ClearedShape>();
        Assert.NotNull(body);
    }

    private sealed record ClearedShape(int Cleared);
}

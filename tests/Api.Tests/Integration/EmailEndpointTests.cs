using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// EMAIL-2 over HTTP through the real app: 401 anonymous on the user-scoped routes; POST is refused
/// (tokens never arrive in a body); authorize on an unconfigured provider → 400 <c>provider_not_configured</c>
/// (the test host carries no OAuth apps); suggested filters; the anonymous callback with a bad or missing
/// state redirects to the client with <c>email_error=consent_failed</c> and creates nothing; uniform 404.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class EmailEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused_ExceptTheConsentCallback()
    {
        var anon = _factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/email/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/email/connections/authorize?provider=google")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/email/connections/suggested-filters")).StatusCode);

        var callback = await anon.GetAsync("/api/email/connections/callback?code=abc&state=not-a-state");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.EndsWith("/email?email_error=consent_failed", callback.Headers.Location!.ToString());
        Assert.EndsWith("/email?email_error=consent_failed", (await anon.GetAsync("/api/email/connections/callback?error=access_denied")).Headers.Location!.ToString());
    }

    [Fact]
    public async Task Member_SeesAnEmptyList_CannotPostTokens_AndGetsClearErrors()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        Assert.Empty((await client.GetFromJsonAsync<List<ConnectionDto>>("/api/email/connections"))!);

        var post = await client.PostAsJsonAsync("/api/email/connections", new { provider = "google", access_token = "x", refresh_token = "y" });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal("use_consent_flow", (await post.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        var unknown = await client.GetAsync("/api/email/connections/authorize?provider=outlook");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("invalid_provider", (await unknown.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        var unconfigured = await client.GetAsync("/api/email/connections/authorize?provider=google");
        Assert.Equal(HttpStatusCode.BadRequest, unconfigured.StatusCode);
        Assert.Equal("provider_not_configured", (await unconfigured.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        var suggested = (await client.GetFromJsonAsync<SuggestedDto>("/api/email/connections/suggested-filters"))!;
        Assert.Contains("notificacion@notificacionesbaccr.com", suggested.SenderFilters);
        Assert.Contains("Voucher Digital", suggested.SubjectFilters);
        Assert.Equal(["BAC", "BN"], suggested.Banks.Select(b => b.Name));

        var missing = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/email/connections/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/email/connections/{missing}/folders")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/email/connections/{missing}", new { subject_filters = new[] { "x" }, polling_interval_minutes = 15 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/email/connections/{missing}")).StatusCode);
    }

    private sealed record ConnectionDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("provider")] string Provider);
    private sealed record ErrorDto([property: JsonPropertyName("error")] string Error, [property: JsonPropertyName("message")] string Message);
    private sealed record BankDto([property: JsonPropertyName("name")] string Name);
    private sealed record SuggestedDto(
        [property: JsonPropertyName("sender_filters")] string[] SenderFilters,
        [property: JsonPropertyName("subject_filters")] string[] SubjectFilters,
        [property: JsonPropertyName("banks")] BankDto[] Banks);
}

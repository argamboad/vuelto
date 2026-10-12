using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>ENV-1 over HTTP (RLS enforced): 401 anonymous, 201/200 for a member, 400 shape, the 409 offer, uniform 404.</summary>
[Collection(IntegrationCollection.Name)]
public class EnvelopeEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/envelopes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/envelopes", NewEnvelope("x"))).StatusCode);
    }

    [Fact]
    public async Task Member_Creates_Lists_Deactivates_AndGetsTheReactivationOffer()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        Assert.Empty((await client.GetFromJsonAsync<List<EnvelopeDto>>("/api/envelopes"))!); // never seeded

        var created = await client.PostAsJsonAsync("/api/envelopes", NewEnvelope("Marchamo"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var envelope = await created.Content.ReadFromJsonAsync<EnvelopeDto>();
        Assert.Equal(718_000m, envelope!.AnnualTargetCrc);
        Assert.Equal("five_week_months", envelope.ReminderCadence);

        var invalid = await client.PostAsJsonAsync("/api/envelopes", NewEnvelope("Bad") with { reminder_cadence = "whenever" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_request", (await invalid.Content.ReadFromJsonAsync<ConflictDto>())!.Error);

        var deactivated = await client.PutAsJsonAsync($"/api/envelopes/{envelope.Id}", new { name = "Marchamo", annual_target_crc = 718_000m, annual_target_usd = 0m, reminder_cadence = "monthly", is_active = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var clash = await client.PostAsJsonAsync("/api/envelopes", NewEnvelope("marchamo"));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var offer = await clash.Content.ReadFromJsonAsync<ConflictDto>();
        Assert.Equal("envelope_exists_inactive", offer!.Error);
        Assert.Equal(envelope.Id, offer.ExistingId);
        Assert.Equal("Marchamo", offer.ExistingName);

        var withInactive = await client.GetFromJsonAsync<List<EnvelopeDto>>("/api/envelopes?include_inactive=true");
        Assert.Contains(withInactive!, e => e.Name == "Marchamo" && !e.IsActive);
    }

    [Fact]
    public async Task ForeignId_Is404()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        var res = await client.PutAsJsonAsync($"/api/envelopes/{Guid.CreateVersion7()}", new { name = "Nope", annual_target_crc = 0m, annual_target_usd = 0m, reminder_cadence = "monthly", is_active = true });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    private static Body NewEnvelope(string name) => new(name, 718_000m, 0m, "five_week_months");

    // Wire-shaped on purpose (snake_case) — the contract the Postman collection documents.
    private sealed record Body(string name, decimal annual_target_crc, decimal annual_target_usd, string reminder_cadence);

    private sealed record EnvelopeDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("annual_target_crc")] decimal AnnualTargetCrc,
        [property: JsonPropertyName("annual_target_usd")] decimal AnnualTargetUsd,
        [property: JsonPropertyName("reminder_cadence")] string ReminderCadence,
        [property: JsonPropertyName("is_active")] bool IsActive);

    private sealed record ConflictDto(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("existing_id")] Guid? ExistingId,
        [property: JsonPropertyName("existing_name")] string? ExistingName);
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>LEDGER-3 over HTTP through the real app (RLS enforced): flagged create → month refunds → mark received books the inflow → revert removes it; 401 / 400 / 404 / 409 contract.</summary>
[Collection(IntegrationCollection.Name)]
public class RefundEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync($"/api/refunds/{Guid.CreateVersion7()}", new { status = "received" })).StatusCode);
    }

    [Fact]
    public async Task Member_FlagsARefund_MarksItReceived_AndReverts()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);
        var category = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))![0];
        var bank = (await client.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];

        var created = await client.PostAsJsonAsync("/api/transactions", new
        {
            payee = "Hospital", bank_id = bank.Id, original_amount = 50_000m, currency = "CRC", transaction_date = "2026-06-05",
            category_id = category.Id, transaction_type = "unplanned_essential", exchange_rate = 500m, refund_expected = true, refund_percentage = 50m,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tx = (await created.Content.ReadFromJsonAsync<TxDto>())!;
        Assert.True(tx.RefundExpected);
        Assert.Equal(50m, tx.RefundPercentage);

        var refunds = (await client.GetFromJsonAsync<List<RefundDto>>($"/api/months/{tx.MonthId}/refunds"))!;
        var refund = Assert.Single(refunds);
        Assert.Equal((25_000m, 50m, "pending"), (refund.AmountCrc, refund.AmountUsd, refund.Status));

        var received = await client.PutAsJsonAsync($"/api/refunds/{refund.Id}", new { status = "received", received_date = "2026-06-20" }); // same month as the purchase
        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        var flipped = (await received.Content.ReadFromJsonAsync<RefundDto>())!;
        Assert.Equal("received", flipped.Status);
        Assert.NotNull(flipped.InflowTransactionId);

        var rows = (await client.GetFromJsonAsync<List<RowDto>>($"/api/months/{tx.MonthId}/transactions"))!;
        Assert.Contains(rows, r => r.Source == "refund_realization" && r.TransactionType == "inflow" && r.AmountCrc == 25_000m);

        var derivedEdit = await client.DeleteAsync($"/api/transactions/{flipped.InflowTransactionId}");
        Assert.Equal(HttpStatusCode.BadRequest, derivedEdit.StatusCode);
        Assert.Equal("derived_transaction", (await derivedEdit.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        var invalid = await client.PutAsJsonAsync($"/api/refunds/{refund.Id}", new { status = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var reverted = await client.PutAsJsonAsync($"/api/refunds/{refund.Id}", new { status = "pending" });
        Assert.Equal(HttpStatusCode.OK, reverted.StatusCode);
        rows = (await client.GetFromJsonAsync<List<RowDto>>($"/api/months/{tx.MonthId}/transactions"))!;
        Assert.DoesNotContain(rows, r => r.TransactionType == "inflow");

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/refunds/{Guid.CreateVersion7()}", new { status = "received" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/months/{Guid.CreateVersion7()}/refunds")).StatusCode);
    }

    private sealed record NamedDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
    private sealed record TxDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("month_id")] Guid MonthId,
        [property: JsonPropertyName("refund_expected")] bool RefundExpected,
        [property: JsonPropertyName("refund_percentage")] decimal? RefundPercentage);
    private sealed record RefundDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("amount_crc")] decimal AmountCrc,
        [property: JsonPropertyName("amount_usd")] decimal AmountUsd,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("inflow_transaction_id")] Guid? InflowTransactionId);
    private sealed record RowDto(
        [property: JsonPropertyName("transaction_type")] string TransactionType,
        [property: JsonPropertyName("amount_crc")] decimal AmountCrc,
        [property: JsonPropertyName("source")] string Source);
    private sealed record ErrorDto([property: JsonPropertyName("error")] string Error, [property: JsonPropertyName("message")] string Message);
}

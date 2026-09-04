using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Vouchers;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// EMAIL-5/6 over HTTP through the real app (RLS enforced): 401 anonymous; a member manages merchant rules
/// (201 / 409 <c>mapping_exists</c> / 400 / 200 / 204 / uniform 404); the review queue lists and counts a
/// staged draft, confirm books an <c>email</c> transaction visible in the month (the rate resolved through
/// the chain's last tier — the household's most recent transaction), a second confirm and a discard are
/// 409 <c>not_pending</c>, and a foreign draft is a uniform 404.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ReviewEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/merchant-mappings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/pending-vouchers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/pending-vouchers/count")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync($"/api/pending-vouchers/{Guid.CreateVersion7()}/confirm", new { })).StatusCode);
    }

    [Fact]
    public async Task Member_ManagesMerchantRules_WithTheConflictContract()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);
        var categories = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))!;

        var created = await client.PostAsJsonAsync("/api/merchant-mappings", new { merchant_pattern = " AutoMercado ", category_id = categories[0].Id, suggested_class = "extraordinary" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rule = (await created.Content.ReadFromJsonAsync<MappingDto>())!;
        Assert.Equal(("AutoMercado", categories[0].Name, "extraordinary"), (rule.MerchantPattern, rule.CategoryName, rule.SuggestedClass));

        var dupe = await client.PostAsJsonAsync("/api/merchant-mappings", new { merchant_pattern = "AUTOMERCADO", category_id = categories[1].Id });
        Assert.Equal(HttpStatusCode.Conflict, dupe.StatusCode);
        Assert.Equal("mapping_exists", (await dupe.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        var invalid = await client.PostAsJsonAsync("/api/merchant-mappings", new { merchant_pattern = "X", category_id = categories[0].Id, suggested_class = "inflow" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_request", (await invalid.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        var updated = await client.PutAsJsonAsync($"/api/merchant-mappings/{rule.Id}", new { merchant_pattern = "Auto Mercado", category_id = categories[1].Id });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(("Auto Mercado", categories[1].Name, null), ((await updated.Content.ReadFromJsonAsync<MappingDto>())!).Let(m => (m.MerchantPattern, m.CategoryName, m.SuggestedClass)));

        var list = (await client.GetFromJsonAsync<List<MappingDto>>("/api/merchant-mappings"))!;
        Assert.Equal("Auto Mercado", Assert.Single(list).MerchantPattern);

        var missing = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/merchant-mappings/{missing}", new { merchant_pattern = "X", category_id = categories[0].Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/merchant-mappings/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/merchant-mappings/{rule.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<MappingDto>>("/api/merchant-mappings"))!);

        // Another household never sees the rule (uniform 404) — nor its categories.
        var stranger = _factory.CreateClientFor(await _factory.SeedUserAsync(TenantRoles.Member));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/merchant-mappings/{rule.Id}")).StatusCode);
        var foreignCategory = await stranger.PostAsJsonAsync("/api/merchant-mappings", new { merchant_pattern = "X", category_id = categories[0].Id });
        Assert.Equal(HttpStatusCode.BadRequest, foreignCategory.StatusCode);
    }

    [Fact]
    public async Task Member_ConfirmsAStagedDraft_IntoAnEmailTransaction_AndTheQueueContractHolds()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);
        var category = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))![0];
        var bank = (await client.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];

        Assert.Empty((await client.GetFromJsonAsync<List<VoucherDto>>("/api/pending-vouchers"))!);
        Assert.Equal(0, (await client.GetFromJsonAsync<CountDto>("/api/pending-vouchers/count"))!.Count);
        var missing = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/pending-vouchers/{missing}/confirm", new { category_id = category.Id, transaction_class = "budgeted" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/pending-vouchers/{missing}/discard", null)).StatusCode);

        // The chain's last tier: a manual transaction with a frozen rate makes confirm resolvable in the test host (no rate provider).
        var manual = await client.PostAsJsonAsync("/api/transactions", new
        {
            payee = "Seed", bank_id = bank.Id, original_amount = 1000m, currency = "CRC", transaction_date = "2026-06-05",
            category_id = category.Id, transaction_type = "budgeted", exchange_rate = 500m,
        });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        var draftId = await SeedDraftAsync(member.TenantId, bank.Id);
        var queue = (await client.GetFromJsonAsync<List<VoucherDto>>("/api/pending-vouchers"))!;
        var draft = Assert.Single(queue);
        Assert.Equal((draftId, "TACO BELL PLAZA REAL C", 7620m, "CRC", bank.Id), (draft.Id, draft.Merchant, draft.Amount, draft.Currency, draft.BankId));
        Assert.Equal(1, (await client.GetFromJsonAsync<CountDto>("/api/pending-vouchers/count"))!.Count);

        var badClass = await client.PostAsJsonAsync($"/api/pending-vouchers/{draftId}/confirm", new { category_id = category.Id, transaction_class = "inflow" });
        Assert.Equal(HttpStatusCode.BadRequest, badClass.StatusCode);

        var confirmed = await client.PostAsJsonAsync($"/api/pending-vouchers/{draftId}/confirm", new { category_id = category.Id, transaction_class = "extraordinary", remember_merchant = true });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var result = (await confirmed.Content.ReadFromJsonAsync<ConfirmDto>())!;
        Assert.Equal((7620m, 15.24m, true), (result.AmountCrc, result.AmountUsd, result.Remembered));

        var rows = (await client.GetFromJsonAsync<List<RowDto>>($"/api/months/{result.MonthId}/transactions"))!;
        Assert.Contains(rows, r => r.Id == result.TransactionId && r.Source == "email" && r.TransactionType == "extraordinary" && r.AmountCrc == 7620m);
        Assert.Equal(0, (await client.GetFromJsonAsync<CountDto>("/api/pending-vouchers/count"))!.Count);
        Assert.Equal("TACO BELL PLAZA REAL C", Assert.Single((await client.GetFromJsonAsync<List<MappingDto>>("/api/merchant-mappings"))!).MerchantPattern);

        var again = await client.PostAsJsonAsync($"/api/pending-vouchers/{draftId}/confirm", new { category_id = category.Id, transaction_class = "budgeted" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("not_pending", (await again.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/pending-vouchers/{draftId}/discard", null)).StatusCode);

        var second = await SeedDraftAsync(member.TenantId, bank.Id);
        var stranger = _factory.CreateClientFor(await _factory.SeedUserAsync(TenantRoles.Member));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/pending-vouchers/{second}/discard", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/pending-vouchers/{second}/discard", null)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<VoucherDto>>("/api/pending-vouchers"))!);
    }

    /// <summary>Stage a draft the way the poller would — inside the household (EnterTenant), without the mail round-trip.</summary>
    private async Task<Guid> SeedDraftAsync(Guid tenantId, Guid bankId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var now = DateTimeOffset.UtcNow;
        var fingerprint = Guid.CreateVersion7().ToString("N");
        var draft = new PendingVoucher
        {
            TenantId = tenantId, EmailConnectionId = Guid.CreateVersion7(), ProviderMessageId = fingerprint, Fingerprint = fingerprint, ParsedBank = "Bac", BankId = bankId,
            Merchant = "TACO BELL PLAZA REAL C", Amount = 7620m, Currency = "CRC", Date = new DateOnly(2026, 6, 13), Authorization = "662664", TransactionType = "COMPRA",
            Status = PendingVoucherStatuses.Pending, ReceivedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        using (tenants.EnterTenant(tenantId))
        {
            db.Add(draft);
            db.Add(new IngestedVoucher { TenantId = tenantId, Fingerprint = fingerprint, PendingVoucherId = draft.Id, CreatedAt = now });
            await db.SaveChangesAsync();
        }
        return draft.Id;
    }

    private sealed record NamedDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
    private sealed record ErrorDto([property: JsonPropertyName("error")] string Error, [property: JsonPropertyName("message")] string Message);
    private sealed record MappingDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("merchant_pattern")] string MerchantPattern, [property: JsonPropertyName("category_name")] string? CategoryName, [property: JsonPropertyName("suggested_class")] string? SuggestedClass);
    private sealed record VoucherDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("merchant")] string? Merchant, [property: JsonPropertyName("amount")] decimal? Amount, [property: JsonPropertyName("currency")] string? Currency, [property: JsonPropertyName("bank_id")] Guid? BankId);
    private sealed record CountDto([property: JsonPropertyName("count")] int Count);
    private sealed record ConfirmDto([property: JsonPropertyName("transaction_id")] Guid TransactionId, [property: JsonPropertyName("month_id")] Guid MonthId, [property: JsonPropertyName("amount_crc")] decimal AmountCrc, [property: JsonPropertyName("amount_usd")] decimal AmountUsd, [property: JsonPropertyName("remembered")] bool Remembered);
    private sealed record RowDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("source")] string Source, [property: JsonPropertyName("transaction_type")] string TransactionType, [property: JsonPropertyName("amount_crc")] decimal AmountCrc);
}

file static class Extensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}

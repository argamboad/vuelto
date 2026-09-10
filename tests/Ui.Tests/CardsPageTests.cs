using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>CARDS-1 page: lists cards with the auto-named badge, creates one (brand + last four + bank), renames one (alias, bank, state).</summary>
public class CardsPageTests : ComponentTestBase
{
    private const string Auto = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Named = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Bank = "bbbbbbbb-0000-0000-0000-000000000001";
    private const string Banks = $$"""[{"id":"{{Bank}}","name":"BAC Credomatic","is_active":true}]""";
    private const string List = $$"""
        [{"id":"{{Auto}}","name":"VISA-1234","brand":"VISA","last4":"1234","bank_id":"{{Bank}}","is_active":true,"auto_named":true},
         {"id":"{{Named}}","name":"Tuti's card","brand":"MASTERCARD","last4":"0000","bank_id":null,"is_active":false,"auto_named":false}]
        """;

    private void Stub()
    {
        Http.On(HttpMethod.Get, "/api/banks", Banks);
        Http.On(HttpMethod.Get, "/api/cards", List);
    }

    [Fact]
    public async Task Lists_Cards_WithIdentityBankStatus_AndTheAutoNamedBadge()
    {
        await SignInAsync();
        Stub();

        var cut = Render<Cards>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='cards-row']").Count));
        var rows = cut.FindAll("[data-testid='cards-row']");
        Assert.Contains("VISA-1234", rows[0].TextContent);
        Assert.Contains("VISA ····1234", rows[0].TextContent);
        Assert.Contains("BAC Credomatic", rows[0].TextContent);
        Assert.NotNull(rows[0].QuerySelector("[data-testid='cards-auto']"));
        Assert.Contains("MASTERCARD ····0000", rows[1].TextContent);
        Assert.Contains("Catalog_Inactive", rows[1].TextContent);
        Assert.Null(rows[1].QuerySelector("[data-testid='cards-auto']"));
    }

    [Fact]
    public async Task Create_PostsAliasBrandLastFourAndBank_AndReloads()
    {
        await SignInAsync();
        Stub();
        Http.On(HttpMethod.Post, "/api/cards", $$"""{"id":"{{Named}}","name":"Amex","brand":"AMEX","last4":"0005","bank_id":null,"is_active":true,"auto_named":false}""", HttpStatusCode.Created);

        var cut = Render<Cards>();
        cut.WaitForElement("[data-testid='cards-new']").Click();
        cut.Find("[data-testid='cards-name']").Input("Amex");
        cut.Find("[data-testid='cards-brand']").Change("AMEX");
        cut.Find("[data-testid='cards-last4']").Input("0005");
        cut.Find("[data-testid='cards-bank']").Change(Bank);
        cut.Find("[data-testid='cards-save']").Click();

        cut.WaitForElement("[data-testid='cards-notice']");
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/cards");
        var body = await post.Content!.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Amex\"", body);
        Assert.Contains("\"brand\":\"AMEX\"", body);
        Assert.Contains("\"last4\":\"0005\"", body);
        Assert.Contains($"\"bank_id\":\"{Bank}\"", body);
        Assert.Empty(cut.FindAll("[data-testid='cards-form']"));
    }

    [Fact]
    public async Task Edit_RenamesTheAutoNamedCard_KeepsItsIdentityReadOnly_AndPuts()
    {
        await SignInAsync();
        Stub();
        Http.On(HttpMethod.Put, $"/api/cards/{Auto}", $$"""{"id":"{{Auto}}","name":"Allan's Visa","brand":"VISA","last4":"1234","bank_id":"{{Bank}}","is_active":true,"auto_named":false}""");

        var cut = Render<Cards>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='cards-edit']").Count));
        cut.FindAll("[data-testid='cards-edit']")[0].Click();

        Assert.Equal("VISA ····1234", cut.Find("[data-testid='cards-form-identity']").TextContent.Trim()); // brand + last four are what the bank prints — not edited
        Assert.Empty(cut.FindAll("[data-testid='cards-last4']"));
        cut.Find("[data-testid='cards-name']").Input("Allan's Visa");
        cut.Find("[data-testid='cards-save']").Click();

        cut.WaitForElement("[data-testid='cards-notice']");
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Allan\\u0027s Visa\"", body); // System.Text.Json escapes the apostrophe
        Assert.Contains("\"is_active\":true", body);
    }

    [Fact]
    public async Task RenewedCard_SameCardAs_MergesIntoTheOriginal_AndShowsBothNumbers()
    {
        // The bank printed a new number; the household says "same card". The auto-named duplicate offers the merge, the survivor lists both numbers.
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/banks", Banks);
        Http.On(HttpMethod.Get, "/api/cards", $$"""
            [{"id":"{{Auto}}","name":"VISA-5678","brand":"VISA","last4":"5678","bank_id":null,"is_active":true,"auto_named":true,"identities":[{"brand":"VISA","last4":"5678"}]},
             {"id":"{{Named}}","name":"Allan's Visa","brand":"VISA","last4":"1234","bank_id":"{{Bank}}","is_active":true,"auto_named":false,"identities":[{"brand":"VISA","last4":"1234"}]}]
            """);
        Http.On(HttpMethod.Post, $"/api/cards/{Auto}/merge", $$"""{"id":"{{Named}}","name":"Allan's Visa","brand":"VISA","last4":"5678","bank_id":"{{Bank}}","is_active":true,"auto_named":false,"identities":[{"brand":"VISA","last4":"1234"},{"brand":"VISA","last4":"5678"}]}""");

        var cut = Render<Cards>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='cards-row']").Count));
        Assert.Equal(2, cut.FindAll("[data-testid='cards-same-as']").Count); // any card can be the renewed one

        cut.FindAll("[data-testid='cards-same-as']")[0].Click(); // rows are in API order: the auto-named VISA-5678 first
        cut.Find("[data-testid='cards-merge-into']").Change(Named);
        Http.On(HttpMethod.Get, "/api/cards", $$"""[{"id":"{{Named}}","name":"Allan's Visa","brand":"VISA","last4":"5678","bank_id":"{{Bank}}","is_active":true,"auto_named":false,"identities":[{"brand":"VISA","last4":"1234"},{"brand":"VISA","last4":"5678"}]}]""");
        cut.Find("[data-testid='cards-merge-save']").Click();

        cut.WaitForElement("[data-testid='cards-notice']");
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == $"/api/cards/{Auto}/merge");
        Assert.Contains($"\"into\":\"{Named}\"", await post.Content!.ReadAsStringAsync());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='cards-row']")));
        Assert.Equal("VISA ····1234 · ····5678", cut.Find("[data-testid='cards-row-identity']").TextContent.Trim());
    }

    [Fact]
    public async Task Kind_ShowsOnTheRow_AndEditPostsItWithTheOptInBackfill()
    {
        // CARDS-3: the kind is set once on the card; correcting past transactions is a deliberate extra tick.
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/banks", Banks);
        Http.On(HttpMethod.Get, "/api/cards", $$"""
            [{"id":"{{Auto}}","name":"Allan's Debit","brand":"VISA","last4":"4444","bank_id":null,"is_active":true,"auto_named":false,"kind":"debit","identities":[{"brand":"VISA","last4":"4444"}]}]
            """);
        Http.On(HttpMethod.Put, $"/api/cards/{Auto}", $$"""{"id":"{{Auto}}","name":"Allan's Debit","brand":"VISA","last4":"4444","bank_id":null,"is_active":true,"auto_named":false,"kind":"debit","backfilled":7,"identities":[{"brand":"VISA","last4":"4444"}]}""");

        var cut = Render<Cards>();
        cut.WaitForAssertion(() => Assert.Equal("Cards_KindDebit", cut.Find("[data-testid='cards-row-kind']").TextContent.Trim()));

        cut.Find("[data-testid='cards-edit']").Click();
        Assert.Equal("debit", cut.Find("[data-testid='cards-kind']").GetAttribute("value"));
        cut.Find("[data-testid='cards-backfill']").Change(true);
        cut.Find("[data-testid='cards-save']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Cards_Backfilled[7]", cut.Find("[data-testid='cards-notice']").TextContent));
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"kind\":\"debit\"", body);
        Assert.Contains("\"backfill_payment_method\":true", body);
    }

    [Fact]
    public async Task InactiveClash_OffersReactivate()
    {
        await SignInAsync();
        Stub();
        Http.On(HttpMethod.Post, "/api/cards", $$"""{"error":"card_exists_inactive","message":"'Tuti's card' (MASTERCARD ····0000) already exists but is inactive — reactivate it?","existing_id":"{{Named}}","existing_name":"Tuti's card"}""", HttpStatusCode.Conflict);
        Http.On(HttpMethod.Put, $"/api/cards/{Named}", $$"""{"id":"{{Named}}","name":"Tuti's card","brand":"MASTERCARD","last4":"0000","bank_id":null,"is_active":true,"auto_named":false}""");

        var cut = Render<Cards>();
        cut.WaitForElement("[data-testid='cards-new']").Click();
        cut.Find("[data-testid='cards-name']").Input("tuti's CARD");
        cut.Find("[data-testid='cards-last4']").Input("0000");
        cut.Find("[data-testid='cards-save']").Click();

        cut.WaitForElement("[data-testid='cards-reactivate']").Click();

        cut.WaitForElement("[data-testid='cards-notice']");
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        Assert.Contains("\"name\":\"Tuti\\u0027s card\"", await put.Content!.ReadAsStringAsync()); // the stored alias, not the typed casing
    }
}

using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>ENV-1 page: lists targets + cadence, creates with the wire payload, rejects negatives locally, and turns an inactive clash into a one-click restore.</summary>
public class EnvelopesPageTests : ComponentTestBase
{
    private const string Marchamo = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Castillo = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string List = $$"""
        [{"id":"{{Marchamo}}","name":"Marchamo","annual_target_crc":718000.00,"annual_target_usd":0,"reminder_cadence":"five_week_months","is_active":true},
         {"id":"{{Castillo}}","name":"Castillo","annual_target_crc":0,"annual_target_usd":1200.50,"reminder_cadence":"monthly","is_active":false}]
        """;

    private async Task<IRenderedComponent<Envelopes>> RenderSignedIn()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/envelopes", List);
        return Render<Envelopes>();
    }

    [Fact]
    public async Task Lists_TargetsCadenceAndBadges()
    {
        var cut = await RenderSignedIn();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='env-row']").Count));
        var rows = cut.FindAll("[data-testid='env-row']");
        Assert.Contains("718,000.00", rows[0].TextContent);
        Assert.Contains("Envelopes_CadenceFiveWeek", rows[0].TextContent);
        Assert.Contains("Catalog_Active", rows[0].TextContent);
        Assert.Contains("1,200.50", rows[1].TextContent);
        Assert.Contains("Envelopes_CadenceMonthly", rows[1].TextContent);
        Assert.Contains("Catalog_Inactive", rows[1].TextContent);
        Assert.Contains("include_inactive=true", Assert.Single(Http.Requests, r => r.Method == HttpMethod.Get).RequestUri!.Query);
    }

    [Fact]
    public async Task Create_PostsTheWirePayload_AndReloads()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Post, "/api/envelopes", """{"id":"cccccccc-0000-0000-0000-000000000003","name":"Viaje","annual_target_crc":0,"annual_target_usd":2500,"reminder_cadence":"monthly","is_active":true}""", HttpStatusCode.Created);

        cut.WaitForElement("[data-testid='env-new']").Click();
        cut.Find("[data-testid='env-name']").Input("Viaje");
        cut.Find("[data-testid='env-usd']").Change("2500");
        cut.Find("[data-testid='env-cadence']").Change("monthly");
        cut.Find("[data-testid='env-save']").Click();

        cut.WaitForElement("[data-testid='env-notice']");
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/envelopes");
        var body = await post.Content!.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Viaje\"", body);
        Assert.Contains("\"annual_target_usd\":2500", body);
        Assert.Contains("\"reminder_cadence\":\"monthly\"", body);
        Assert.Empty(cut.FindAll("[data-testid='env-form']"));
        Assert.Equal(2, Http.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task NegativeTarget_IsRejectedLocally_WithoutARequest()
    {
        var cut = await RenderSignedIn();

        cut.WaitForElement("[data-testid='env-new']").Click();
        cut.Find("[data-testid='env-name']").Input("Viaje");
        cut.Find("[data-testid='env-crc']").Change("-5");
        cut.Find("[data-testid='env-save']").Click();

        Assert.Contains("Envelopes_Negative", cut.WaitForElement("[data-testid='env-form-error']").TextContent);
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/envelopes");
    }

    [Fact]
    public async Task InactiveClash_Reactivate_RestoresTheStoredName_WithTheTypedTargets()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Post, "/api/envelopes",
            $$"""{"error":"envelope_exists_inactive","message":"'Castillo' already exists but is inactive — reactivate it?","existing_id":"{{Castillo}}","existing_name":"Castillo"}""",
            HttpStatusCode.Conflict);
        Http.On(HttpMethod.Put, $"/api/envelopes/{Castillo}", $$"""{"id":"{{Castillo}}","name":"Castillo","annual_target_crc":900000,"annual_target_usd":0,"reminder_cadence":"monthly","is_active":true}""");

        cut.WaitForElement("[data-testid='env-new']").Click();
        cut.Find("[data-testid='env-name']").Input("castillo");
        cut.Find("[data-testid='env-crc']").Change("900000");
        cut.Find("[data-testid='env-save']").Click();
        cut.WaitForElement("[data-testid='env-reactivate']").Click();

        cut.WaitForElement("[data-testid='env-notice']");
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Castillo\"", body);          // stored name restored, not the typed "castillo"
        Assert.Contains("\"annual_target_crc\":900000", body);   // the freshly typed target is applied
        Assert.Contains("\"is_active\":true", body);
    }

    [Fact]
    public async Task ActiveClash_ShowsTheMessage_NoReactivateButton()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Post, "/api/envelopes",
            """{"error":"envelope_exists","message":"An envelope named 'Marchamo' already exists","existing_id":null,"existing_name":null}""",
            HttpStatusCode.Conflict);

        cut.WaitForElement("[data-testid='env-new']").Click();
        cut.Find("[data-testid='env-name']").Input("marchamo");
        cut.Find("[data-testid='env-save']").Click();

        Assert.Contains("already exists", cut.WaitForElement("[data-testid='env-form-error']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='env-reactivate']"));
    }
}

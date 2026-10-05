using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests.Pages;

/// <summary>
/// v4 audit T56 (TOOL-7, TB-UI-40..46): the Household page's branches, on the bUnit chassis. Until now each of
/// these was reached only when a browser journey happened to pass through it. The localizer echoes resource
/// KEYS, so an assertion on a key is an assertion on which branch chose the copy.
/// </summary>
public class HouseholdPageTests : ComponentTestBase
{
    private const string Me = "11111111-1111-1111-1111-111111111111"; // TestJwt's default subject
    private const string AnAdmin = "22222222-0000-0000-0000-000000000002";
    private const string AMember = "33333333-0000-0000-0000-000000000003";

    private const string TheOwner = "44444444-0000-0000-0000-000000000004";

    /// <summary>The caller with <paramref name="myRole"/>, plus (unless alone) one person in each other role.</summary>
    private static string Roster(string myRole, bool withOthers = true)
    {
        static string Member(string id, string role) =>
            $$"""{"user_id":"{{id}}","display_name":"{{role}} person","email":"{{role}}@x.com","role":"{{role}}","joined_at":"2026-01-01T00:00:00Z"}""";
        var members = new List<string> { Member(Me, myRole) };
        if (withOthers)
        {
            if (myRole != "owner") members.Add(Member(TheOwner, "owner"));
            if (myRole != "admin") members.Add(Member(AnAdmin, "admin"));
            if (myRole != "member") members.Add(Member(AMember, "member"));
        }
        return $$"""{"id":"99999999-9999-9999-9999-999999999999","name":"Test Household","my_role":"{{myRole}}","members":[{{string.Join(",", members)}}]}""";
    }

    private async Task<IRenderedComponent<Household>> RenderAsync(string myRole = "owner", bool withOthers = true, bool billing = true)
    {
        StubFeatures(billing);
        Http.On(HttpMethod.Get, "/api/household", Roster(myRole, withOthers));
        Http.On(HttpMethod.Get, "/api/household/invitations", "[]");
        await SignInAsync();
        var page = Render<Household>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=member-row]")));
        return page;
    }

    private void Confirm(bool answer) => JSInterop.Setup<bool>("confirm", _ => true).SetResult(answer);

    private static async Task InviteAsync(IRenderedComponent<Household> page)
    {
        page.Find("[data-testid=invite-email]").Change("friend@example.com");
        await page.Find("[data-testid=invite-send]").ClickAsync(new());
    }

    // ── TB-UI-40: the seat-limit copy never acts on a probe that failed ──

    [Fact]
    public async Task SeatLimitCopy_AfterAFailedProbe_AsksAgain_AndOffersTheUpgradeOnABillingOnDeployment()
    {
        Http.OnUnreachable(HttpMethod.Get, "/api/features"); // the server was waking up when the page loaded
        Http.On(HttpMethod.Get, "/api/household", Roster("owner"));
        Http.On(HttpMethod.Get, "/api/household/invitations", "[]");
        Http.On(HttpMethod.Post, "/api/household/invitations", "{}", HttpStatusCode.PaymentRequired);
        await SignInAsync();
        var page = Render<Household>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=invite-send]")));

        StubFeatures(billing: true); // ...and is up by the time the owner hits the cap
        await InviteAsync(page);

        page.WaitForAssertion(() => Assert.Contains("Household_ErrSeatLimit", page.Find("[data-testid=household-status]").TextContent));
        Assert.DoesNotContain("Household_ErrSeatLimitNoBilling", page.Markup); // the stale "off" used to win
    }

    [Fact]
    public async Task SeatLimitCopy_WhileTheProbeStillFails_SaysFull_AndOffersNothing()
    {
        Http.OnUnreachable(HttpMethod.Get, "/api/features");
        Http.On(HttpMethod.Get, "/api/household", Roster("owner"));
        Http.On(HttpMethod.Get, "/api/household/invitations", "[]");
        Http.On(HttpMethod.Post, "/api/household/invitations", "{}", HttpStatusCode.PaymentRequired);
        await SignInAsync();
        var page = Render<Household>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=invite-send]")));

        await InviteAsync(page);

        page.WaitForAssertion(() => Assert.Contains("Household_ErrSeatLimitNoBilling", page.Find("[data-testid=household-status]").TextContent));
    }

    // ── TB-UI-41..43: three error branches only E2E reached ──

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "Household_ErrEmailAlreadyMember")]
    [InlineData(HttpStatusCode.InternalServerError, "Household_ErrSendInvite")]
    [InlineData(HttpStatusCode.BadRequest, "Household_ErrSendInvite")]
    public async Task Invite_Failure_ShowsTheCopyForThatFailure(HttpStatusCode status, string expectedKey)
    {
        var page = await RenderAsync();
        Http.On(HttpMethod.Post, "/api/household/invitations", "{}", status);

        await InviteAsync(page);

        page.WaitForAssertion(() => Assert.Equal(expectedKey, page.Find("[data-testid=household-status]").TextContent.Trim()));
    }

    [Fact]
    public async Task Load_Failure_ShowsErrLoad_AndNoRoster()
    {
        StubFeatures();
        Http.On(HttpMethod.Get, "/api/household", "{}", HttpStatusCode.InternalServerError);
        await SignInAsync();

        var page = Render<Household>();

        page.WaitForAssertion(() => Assert.Equal("Household_ErrLoad", page.Find("[data-testid=household-status]").TextContent.Trim()));
        Assert.Empty(page.FindAll("[data-testid=member-row]"));
    }

    [Fact]
    public async Task Anonymous_IsSentToLogin_AndNothingIsRequested()
    {
        var page = Render<Household>();

        Assert.EndsWith("/login", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/household"));
        _ = page;
    }

    // ── TB-UI-44: the client mirror of the role matrix (ADR-009) ──

    [Theory]
    //          role      rename invite remove roles  export transfer leave
    [InlineData("owner", true, true, true, true, true, true, false)]
    [InlineData("admin", true, true, true, false, false, false, true)]
    [InlineData("member", false, false, false, false, false, false, true)]
    public async Task Controls_FollowTheRoleMatrix(string role, bool rename, bool invite, bool remove, bool roles, bool export, bool transfer, bool leave)
    {
        var page = await RenderAsync(role);
        bool Has(string testId) => page.FindAll($"[data-testid={testId}]").Count > 0;

        Assert.Equal(rename, Has("household-rename-save"));
        Assert.Equal(invite, Has("invite-send"));
        Assert.Equal(remove, Has("member-remove"));
        Assert.Equal(roles, Has("member-promote") || Has("member-demote"));
        Assert.Equal(export, Has("export-data"));
        Assert.Equal(transfer, Has("transfer-submit"));
        Assert.Equal(leave, Has("leave-household"));
        Assert.False(Has("leave-dissolve")); // only a sole owner dissolves
        if (!invite) Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/household/invitations"); // not even fetched
    }

    [Fact]
    public async Task ASoleOwner_IsOfferedDissolve_NotTransfer()
    {
        var page = await RenderAsync("owner", withOthers: false);

        Assert.NotEmpty(page.FindAll("[data-testid=leave-dissolve]"));
        Assert.Empty(page.FindAll("[data-testid=transfer-submit]"));
        Assert.Empty(page.FindAll("[data-testid=leave-household]"));
    }

    [Fact]
    public async Task NobodyGetsControls_OnTheirOwnRow_OrOnTheOwners()
    {
        var page = await RenderAsync("admin");

        // An admin sees: self (no controls), the owner (no controls), a member (remove only — roles are the owner's).
        var rowsWithRemove = page.FindAll("[data-testid=member-row]").Count(r => r.QuerySelector("[data-testid=member-remove]") is not null);
        Assert.Equal(1, rowsWithRemove);
    }

    // ── TB-UI-45: the export URL contract the native launcher depends on ──

    [Theory]
    [InlineData("/api/files/abc", "http://localhost/api/files/abc")]                       // local disk: relative, resolved against the API base
    [InlineData("https://s3.example.com/bucket/x?sig=1", "https://s3.example.com/bucket/x?sig=1")] // S3: absolute, passed through untouched
    public async Task Export_HandsTheLauncherAnAbsoluteUrl(string downloadUrl, string expected)
    {
        var page = await RenderAsync();
        Http.On(HttpMethod.Post, "/api/household/export", $$"""{"download_url":"{{downloadUrl}}"}""");

        await page.Find("[data-testid=export-data]").ClickAsync(new());
        await page.WaitForElement("[data-testid=export-download]").ClickAsync(new());

        var (url, fallbackName) = Assert.Single(DownloadLauncher.Launched);
        Assert.Equal(expected, url);
        Assert.Equal("household-export.json", fallbackName);
    }

    [Fact]
    public async Task Export_Failure_ShowsErrExport_AndOffersNoDownload()
    {
        var page = await RenderAsync();
        Http.On(HttpMethod.Post, "/api/household/export", "{}", HttpStatusCode.InternalServerError);

        await page.Find("[data-testid=export-data]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal("Household_ErrExport", page.Find("[data-testid=household-status]").TextContent.Trim()));
        Assert.Empty(page.FindAll("[data-testid=export-download]"));
    }

    // ── TB-UI-46: leave / transfer / dissolve — failure copy and the success exits ──

    [Fact]
    public async Task Leave_Failure_ShowsErrLeave_AndStaysOnThePage()
    {
        var page = await RenderAsync("member");
        Confirm(true);
        Http.On(HttpMethod.Post, "/api/household/leave", "{}", HttpStatusCode.InternalServerError);

        await page.Find("[data-testid=leave-household]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal("Household_ErrLeave", page.Find("[data-testid=household-status]").TextContent.Trim()));
        Assert.NotEmpty(page.FindAll("[data-testid=member-row]"));
    }

    [Fact]
    public async Task Leave_Declined_SendsNothing()
    {
        var page = await RenderAsync("member");
        Confirm(false);

        await page.Find("[data-testid=leave-household]").ClickAsync(new());

        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/household/leave");
    }

    [Fact]
    public async Task Leave_Success_GoesHome()
    {
        var page = await RenderAsync("member");
        Confirm(true);
        Http.On(HttpMethod.Post, "/api/household/leave", "{}");

        await page.Find("[data-testid=leave-household]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal("http://localhost/", Services.GetRequiredService<NavigationManager>().Uri));
    }

    [Fact]
    public async Task Dissolve_Failure_ShowsErrDissolve_AndSendsTheConfirmFlag()
    {
        var page = await RenderAsync("owner", withOthers: false);
        Confirm(true);
        Http.On(HttpMethod.Post, "/api/household/leave", "{}", HttpStatusCode.InternalServerError);

        await page.Find("[data-testid=leave-dissolve]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal("Household_ErrDissolve", page.Find("[data-testid=household-status]").TextContent.Trim()));
        var sent = await Http.Requests.Last(r => r.RequestUri!.AbsolutePath == "/api/household/leave").Content!.ReadAsStringAsync();
        Assert.Contains("\"confirm_dissolve\":true", sent);
    }

    [Fact]
    public async Task Transfer_Failure_ShowsErrTransfer_AndTheButtonNeedsATarget()
    {
        var page = await RenderAsync("owner");
        Assert.True(page.Find("[data-testid=transfer-submit]").HasAttribute("disabled")); // nobody chosen yet
        Http.On(HttpMethod.Post, "/api/household/transfer-ownership", "{}", HttpStatusCode.InternalServerError);

        page.Find("[data-testid=transfer-select]").Change(AMember);
        await page.Find("[data-testid=transfer-submit]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal("Household_ErrTransfer", page.Find("[data-testid=household-status]").TextContent.Trim()));
        var sent = await Http.Requests.Last(r => r.RequestUri!.AbsolutePath == "/api/household/transfer-ownership").Content!.ReadAsStringAsync();
        Assert.Contains(AMember, sent);
    }
}

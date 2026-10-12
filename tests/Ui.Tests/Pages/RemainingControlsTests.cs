using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Layout;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests.Pages;

/// <summary>
/// v4 audit T59 (TB-DOC-5, R149): the controls whose <c>data-testid</c> no test referenced. An id nothing uses
/// can be renamed or dropped without a single failure; the test-id contract gate
/// (<c>TestIdContractTests</c>) requires every id to be used, and these are the tests that use the twelve
/// that were not — each drives the control, not just its presence.
/// </summary>
public class RemainingControlsTests : ComponentTestBase
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string MemberId = "55555555-0000-0000-0000-000000000005";
    private const string InvitationId = "66666666-0000-0000-0000-000000000006";

    private void Confirm(bool answer) => JSInterop.Setup<bool>("confirm", _ => true).SetResult(answer);

    // ── Notification bell: delete one, clear the read ones, clear all ──

    private async Task<IRenderedComponent<NotificationBell>> OpenBellAsync()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/notifications/unread-count", """{"count":1}""");
        Http.On(HttpMethod.Get, "/api/notifications", """
            [{"id":"aaaaaaaa-0000-0000-0000-000000000001","kind":"x","title":"Unread","body":"","read_at":null,"created_at":"2026-01-01T00:00:00Z"},
             {"id":"aaaaaaaa-0000-0000-0000-000000000002","kind":"x","title":"Read","body":"","read_at":"2026-01-02T00:00:00Z","created_at":"2026-01-01T00:00:00Z"}]
            """);
        var cut = Render<NotificationBell>();
        cut.Find("[data-testid='notif-bell']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='notif-item']").Count));
        return cut;
    }

    [Fact]
    public async Task Bell_DeleteAnUnreadItem_RemovesIt_AndTheBadgeDrops()
    {
        var cut = await OpenBellAsync();
        Http.On(HttpMethod.Delete, "/api/notifications/aaaaaaaa-0000-0000-0000-000000000001", "", HttpStatusCode.NoContent);

        await cut.FindAll("[data-testid='notif-delete']")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='notif-item']")));
        Assert.Empty(cut.FindAll("[data-testid='notif-count']")); // the only unread one is gone
    }

    [Fact]
    public async Task Bell_ClearRead_AsksTheServerForReadOnly_AndReloadsTheList()
    {
        var cut = await OpenBellAsync();
        Http.On(HttpMethod.Delete, "/api/notifications", "", HttpStatusCode.NoContent);

        await cut.Find("[data-testid='notif-clear-read']").ClickAsync(new());

        var sent = Http.Requests.Last(r => r.Method == HttpMethod.Delete);
        Assert.Equal("?read=true", sent.RequestUri!.Query);
        Assert.Equal(2, Http.Requests.Count(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/notifications")); // re-fetched
    }

    [Fact]
    public async Task Bell_ClearAll_NamesItsScopeExplicitly_AndEmptiesTheList()
    {
        var cut = await OpenBellAsync();
        Http.On(HttpMethod.Delete, "/api/notifications", "", HttpStatusCode.NoContent);

        await cut.Find("[data-testid='notif-clear-all']").ClickAsync(new());

        Assert.Equal("?read=false", Http.Requests.Last(r => r.Method == HttpMethod.Delete).RequestUri!.Query); // never a bare DELETE
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='notif-item']")));
        Assert.Empty(cut.FindAll("[data-testid='notif-count']"));
    }

    // ── Household: regenerate a pending invitation ──

    [Fact]
    public async Task Household_RegenerateInvite_RevealsTheNewToken()
    {
        StubFeatures();
        Http.On(HttpMethod.Get, "/api/household", """{"id":"99999999-9999-9999-9999-999999999999","name":"Test Household","my_role":"owner","members":[]}""");
        Http.On(HttpMethod.Get, "/api/household/invitations", $$"""[{"invitation_id":"{{InvitationId}}","invited_email":"guest@x.com","expires_at":"2026-12-01T00:00:00Z"}]""");
        Http.On(HttpMethod.Post, $"/api/household/invitations/{InvitationId}/regenerate", """{"token":"fresh-token","invited_email":"guest@x.com"}""");
        await SignInAsync();
        var page = Render<Household>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid=pending-invite-row]")));

        await page.Find("[data-testid=invite-regenerate]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal("fresh-token", page.Find("[data-testid=invite-token]").TextContent.Trim()));
    }

    // ── Admin console: broadcast, targeted announcement, MFA reset ──

    private async Task<IRenderedComponent<AdminConsole>> OpenConsoleAsync(bool openTenant)
    {
        StubFeatures();
        Http.On(HttpMethod.Get, "/api/admin/me", """{"is_staff":true}""");
        Http.On(HttpMethod.Get, "/api/admin/tenants", $$"""[{"id":"{{TenantId}}","name":"Smiths","member_count":1,"created_at":"2026-01-01T00:00:00Z"}]""");
        Http.On(HttpMethod.Get, $"/api/admin/tenants/{TenantId}",
            $$"""{"id":"{{TenantId}}","name":"Smiths","created_at":"2026-01-01T00:00:00Z","subscription_status":"none","plan_key":"free","provider_managed":false,"audit_event_count":0,"members":[{"user_id":"{{MemberId}}","display_name":"Sam Smith","email":"sam@x.com","role":"owner"}]}""");
        await SignInAsync();
        var cut = Render<AdminConsole>();
        var row = cut.WaitForElement("[data-testid=admin-tenant-row]");
        if (openTenant)
        {
            row.Click();
            cut.WaitForElement("[data-testid=admin-plan]");
        }
        return cut;
    }

    [Fact]
    public async Task Admin_Broadcast_Confirmed_IsQueued_AndTheFormClears()
    {
        var cut = await OpenConsoleAsync(openTenant: false);
        Confirm(true);
        Http.On(HttpMethod.Post, "/api/admin/announce-all", "{}", HttpStatusCode.Accepted);

        cut.Find("[data-testid=admin-broadcast-title]").Change("Maintenance");
        cut.Find("[data-testid=admin-broadcast-body]").Change("Tonight at ten.");
        await cut.Find("[data-testid=admin-broadcast-send]").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal("Admin_BroadcastQueued", cut.Find("[data-testid=admin-broadcast-status]").TextContent.Trim()));
        var sent = await Http.Requests.Last(r => r.RequestUri!.AbsolutePath == "/api/admin/announce-all").Content!.ReadAsStringAsync();
        Assert.Contains("\"title\":\"Maintenance\"", sent);
    }

    [Fact]
    public async Task Admin_Broadcast_Declined_SendsNothing()
    {
        var cut = await OpenConsoleAsync(openTenant: false);
        Confirm(false);
        cut.Find("[data-testid=admin-broadcast-title]").Change("Maintenance");
        cut.Find("[data-testid=admin-broadcast-body]").Change("Tonight at ten.");

        await cut.Find("[data-testid=admin-broadcast-send]").ClickAsync(new());

        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/admin/announce-all");
    }

    [Fact]
    public async Task Admin_Announce_ToACheckedMember_SendsThatMembersId()
    {
        var cut = await OpenConsoleAsync(openTenant: true);
        Confirm(true);
        Http.On(HttpMethod.Post, $"/api/admin/tenants/{TenantId}/announce", """{"notified_count":1}""");

        cut.Find("[data-testid=admin-member-check]").Change(true);
        cut.Find("[data-testid=admin-announce-title]").Change("Hello");
        cut.Find("[data-testid=admin-announce-body]").Change("Just you.");
        await cut.Find("[data-testid=admin-announce-send]").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/announce")));
        var sent = await Http.Requests.Last(r => r.RequestUri!.AbsolutePath.EndsWith("/announce")).Content!.ReadAsStringAsync();
        Assert.Contains(MemberId, sent); // targeted, not the whole household
    }

    [Fact]
    public async Task Admin_MfaReset_Confirmed_ReportsWhoWasReset()
    {
        var cut = await OpenConsoleAsync(openTenant: true);
        Confirm(true);
        Http.On(HttpMethod.Delete, $"/api/admin/users/{MemberId}/mfa", "", HttpStatusCode.NoContent);

        await cut.Find("[data-testid=admin-mfa-reset]").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal("Admin_MfaWasReset[Sam Smith]", cut.Find("[data-testid=admin-mfa-reset-status]").TextContent.Trim()));
    }

    // ── The layout's impersonation banner, and the settings page's preferences card ──

    [Fact]
    public async Task Layout_ShowsTheImpersonationBanner_OnlyWhileImpersonating()
    {
        StubFeatures();
        Http.On(HttpMethod.Get, "/api/admin/me", """{"is_staff":true}""");
        await SignInAsync(name: "Staff Person");
        var layout = Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>body</div>")));
        Assert.Empty(layout.FindAll("[data-testid=impersonation-banner]"));

        Auth.BeginImpersonation(TestJwt.Build(name: "Target User", impersonatedBy: "staff"));
        layout.Render(); // the console navigates home after starting impersonation, which re-renders the layout

        layout.WaitForAssertion(() => Assert.Contains("Impersonation_Banner[Target User]", layout.Find("[data-testid=impersonation-banner]").TextContent));
    }

    [Fact]
    public async Task Settings_HasThePreferencesCard_WithBothSwitchers()
    {
        Http.On(HttpMethod.Get, "/api/auth/providers", """{"providers":[]}""");
        Http.On(HttpMethod.Get, "/api/auth/logins", "[]");
        Http.On(HttpMethod.Get, "/api/auth/mfa", """{"enabled":false}""");
        Http.On(HttpMethod.Get, "/api/notifications/preferences", """{"in_app":true,"email":true}""");
        await SignInAsync();

        var cut = Render<Settings>();

        var card = cut.WaitForElement("[data-testid=preferences-card]");
        Assert.Contains("Settings_Preferences", card.TextContent);
        Assert.True(card.QuerySelectorAll("select, button").Length >= 2, "the language and theme switchers live in this card");
    }
}

using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests.Pages;

/// <summary>
/// v4 audit T56 (TB-UI-61..64 + the per-page floor): the admin console's comp, revert and impersonate
/// branches — the browser journeys drive announce and the forbidden state only — and one test each for the
/// three pages that had none (<c>/auth-error</c>, <c>/auth-callback</c>, <c>/settings</c>).
/// </summary>
public class AdminConsoleAndSmallPagesTests : ComponentTestBase
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string MemberId = "55555555-0000-0000-0000-000000000005";
    private string Location => Services.GetRequiredService<NavigationManager>().Uri;

    private static string Detail(string plan) =>
        $$"""{"id":"{{TenantId}}","name":"Smiths","created_at":"2026-01-01T00:00:00Z","subscription_status":"none","plan_key":"{{plan}}","provider_managed":false,"audit_event_count":0,"members":[{"user_id":"{{MemberId}}","display_name":"Sam Smith","email":"sam@x.com","role":"owner"}]}""";

    private async Task<IRenderedComponent<AdminConsole>> OpenTenantAsync(string plan = "free")
    {
        StubFeatures(billing: true);
        Http.On(HttpMethod.Get, "/api/admin/me", """{"is_staff":true}""");
        Http.On(HttpMethod.Get, "/api/admin/tenants", $$"""[{"id":"{{TenantId}}","name":"Smiths","member_count":1,"created_at":"2026-01-01T00:00:00Z"}]""");
        Http.On(HttpMethod.Get, $"/api/admin/tenants/{TenantId}", Detail(plan));
        await SignInAsync(name: "Staff Person");

        var cut = Render<AdminConsole>();
        cut.WaitForElement("[data-testid=admin-tenant-row]").Click();
        cut.WaitForAssertion(() => Assert.Equal(plan, cut.Find("[data-testid=admin-plan]").TextContent.Trim()));
        return cut;
    }

    private void Confirm(bool answer) => JSInterop.Setup<bool>("confirm", _ => true).SetResult(answer);

    // ── TB-UI-61 ──

    [Fact]
    public async Task Comp_Refused409_ShowsTheSubscriptionError_AndKeepsTheTenantOpen()
    {
        var cut = await OpenTenantAsync();
        Confirm(true);
        Http.On(HttpMethod.Put, $"/api/admin/tenants/{TenantId}/subscription", "{}", HttpStatusCode.Conflict);

        await cut.Find("[data-testid=admin-comp-pro]").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Admin_ErrSubscription", cut.Markup));
        Assert.Equal("free", cut.Find("[data-testid=admin-plan]").TextContent.Trim()); // the detail is still there, unchanged
        Assert.Empty(cut.FindAll("[data-testid=admin-sub-status]"));
    }

    [Fact]
    public async Task Comp_Declined_SendsNothing()
    {
        var cut = await OpenTenantAsync();
        Confirm(false);

        await cut.Find("[data-testid=admin-comp-pro]").ClickAsync(new());

        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Put);
    }

    // ── TB-UI-62 ──

    [Fact]
    public async Task Revert_Succeeds_ReloadsThePlan_AndTheRevertButtonIsGone()
    {
        var cut = await OpenTenantAsync(plan: "pro");
        Confirm(true);
        Http.On(HttpMethod.Delete, $"/api/admin/tenants/{TenantId}/subscription", "", HttpStatusCode.NoContent);
        Http.On(HttpMethod.Get, $"/api/admin/tenants/{TenantId}", Detail("free")); // what the reload will see

        await cut.Find("[data-testid=admin-revert-free]").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal("free", cut.Find("[data-testid=admin-plan]").TextContent.Trim()));
        Assert.Equal("Admin_SubscriptionUpdated", cut.Find("[data-testid=admin-sub-status]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=admin-revert-free]"));  // nothing left to revert: a second click cannot happen
        Assert.NotEmpty(cut.FindAll("[data-testid=admin-comp-pro]"));
    }

    // ── TB-UI-63 ──

    [Fact]
    public async Task Impersonate_Declined_DoesNothing()
    {
        var cut = await OpenTenantAsync();
        Confirm(false);

        await cut.Find("[data-testid=admin-impersonate]").ClickAsync(new());

        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/admin/impersonate"));
        Assert.False(Auth.IsImpersonating);
    }

    [Fact]
    public async Task Impersonate_Accepted_EntersImpersonation_AndGoesHome()
    {
        var cut = await OpenTenantAsync();
        Confirm(true);
        var token = TestJwt.Build(userId: MemberId, name: "Sam Smith", impersonatedBy: "staff");
        Http.On(HttpMethod.Post, $"/api/admin/impersonate/{MemberId}", $$"""{"access_token":"{{token}}","expires_in":600}""");

        await cut.Find("[data-testid=admin-impersonate]").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.True(Auth.IsImpersonating));
        Assert.Equal("Sam Smith", Auth.DisplayName);
        Assert.Equal("http://localhost/", Location);
    }

    [Fact]
    public async Task Impersonate_Refused_ShowsTheError_AndStaysStaff()
    {
        var cut = await OpenTenantAsync();
        Confirm(true);
        Http.On(HttpMethod.Post, $"/api/admin/impersonate/{MemberId}", "{}", HttpStatusCode.Forbidden);

        await cut.Find("[data-testid=admin-impersonate]").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Admin_ErrImpersonate", cut.Markup));
        Assert.False(Auth.IsImpersonating);
    }

    // ── TB-UI-64 ──

    [Fact]
    public async Task NonStaff_SeesForbidden_AndNoTenantIsListedOrFetched()
    {
        StubFeatures();
        Http.On(HttpMethod.Get, "/api/admin/me", """{"is_staff":false}""");
        await SignInAsync();

        var cut = Render<AdminConsole>();

        cut.WaitForElement("[data-testid=admin-forbidden]");
        Assert.Empty(cut.FindAll("[data-testid=admin-tenant-row]"));
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/admin/tenants");
    }

    // ── The per-page floor: the three pages that had no component test ──

    [Fact]
    public void AuthError_ExplainsAndLinksBackToSignIn()
    {
        var cut = Render<AuthError>();

        Assert.Contains("AuthError_Heading", cut.Markup);
        Assert.Equal("/login", cut.Find("a").GetAttribute("href"));
    }

    [Theory]
    [InlineData("/join?token=abc", "http://localhost/join?token=abc")] // a same-origin path is honoured
    [InlineData("//evil.example/x", "http://localhost/")]             // protocol-relative: an open redirect, refused
    [InlineData("https://evil.example/x", "http://localhost/")]       // absolute: refused
    [InlineData(null, "http://localhost/")]
    public async Task AuthCallback_HonoursOnlyASameOriginReturnPath(string? stored, string expected)
    {
        await SignInAsync();
        JSInterop.Setup<string?>("localStorage.getItem", "post_login_redirect").SetResult(stored);

        Render<AuthCallback>();

        Assert.Equal(expected, Location);
    }

    [Fact]
    public void AuthCallback_WithNoSession_GoesToTheErrorPage()
    {
        Http.On(HttpMethod.Post, "/api/auth/refresh", """{"error":"invalid_refresh_token"}""", HttpStatusCode.Unauthorized);

        var cut = Render<AuthCallback>();

        cut.WaitForAssertion(() => Assert.EndsWith("/auth-error", Location));
    }

    [Fact]
    public void Settings_Anonymous_IsSentToLogin()
    {
        Render<Settings>();

        Assert.EndsWith("/login", Location);
    }

    [Fact]
    public async Task Settings_SignedIn_ListsTheLinkedSignIns()
    {
        Http.On(HttpMethod.Get, "/api/auth/providers", """{"providers":["google"]}""");
        Http.On(HttpMethod.Get, "/api/auth/logins", """[{"provider":"google","linked_at":"2026-01-01T00:00:00Z"}]""");
        Http.On(HttpMethod.Get, "/api/auth/mfa", """{"enabled":false}""");
        Http.On(HttpMethod.Get, "/api/notifications/preferences", """{"in_app":true,"email":true}""");
        await SignInAsync();

        var cut = Render<Settings>();

        cut.WaitForAssertion(() => Assert.Contains("Settings_Title", cut.Markup));
        Assert.Contains(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/auth/logins");
        Assert.DoesNotContain("/login", Location);
    }

    // ── The HTTP stub's new answers (3xx with Location; a delay that honours cancellation) ──

    [Fact]
    public async Task Stub_Redirect_CarriesItsLocation_AndIsNotFollowed()
    {
        Http.OnRedirect(HttpMethod.Get, "/api/files/x", "https://elsewhere.example/y");
        using var client = new HttpClient(Http) { BaseAddress = new Uri("http://localhost") };

        var response = await client.GetAsync("/api/files/x");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://elsewhere.example/y", response.Headers.Location?.ToString());
        Assert.Single(Http.Requests); // the test handler answers; nothing chased the Location
    }

    [Fact]
    public async Task Stub_Delayed_AnswersAfterTheDelay_AndACancelledCallerStopsWaiting()
    {
        var clock = new FakeTimeProvider();
        Http.OnDelayed(HttpMethod.Get, "/api/slow", TimeSpan.FromSeconds(30), clock, """{"ok":true}""");
        using var client = new HttpClient(Http) { BaseAddress = new Uri("http://localhost") };

        using var impatient = new CancellationTokenSource();
        var abandoned = client.GetAsync("/api/slow", impatient.Token);
        impatient.Cancel(); // the caller's timeout fires first
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        var patient = client.GetAsync("/api/slow");
        Assert.False(patient.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, (await patient).StatusCode);
    }
}

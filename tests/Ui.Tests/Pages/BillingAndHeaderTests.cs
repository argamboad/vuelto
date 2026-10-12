using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests.Pages;

/// <summary>
/// v4 audit T56 (TB-UI-52..59): the Billing page's refetch loop and its gate, and the header's links. The
/// common thread is BILL-7/UX-13: <c>GET /api/features</c> failing is not the deployment saying "billing is
/// off" — a page that acts on it bounces a paying owner home, and a header that asks once hides the link for
/// the whole session.
/// </summary>
public class BillingAndHeaderTests : ComponentTestBase
{
    private const string Free = """{"plan_key":"free","status":"active"}""";
    private int BillingFetches() => Http.Requests.Count(r => r.RequestUri?.AbsolutePath == "/api/billing");
    private string Location => Services.GetRequiredService<NavigationManager>().Uri;

    // ── TB-UI-52 ──

    [Fact]
    public async Task ReturnFromCheckout_WhenTheWebhookNeverLands_RefetchesTwice_ThenStops()
    {
        await SignInAsync();
        StubFeatures();
        Http.On(HttpMethod.Get, "/api/billing", Free);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/billing/success");

        var cut = Render<Billing>();
        cut.WaitForElement("[data-testid='billing-plan']");
        Assert.Equal(1, BillingFetches());

        for (var refetch = 2; refetch <= 3; refetch++)
        {
            Time.Advance(TimeSpan.FromSeconds(3));
            cut.WaitForAssertion(() => Assert.Equal(refetch, BillingFetches()));
        }

        Time.Advance(TimeSpan.FromMinutes(5)); // the loop is bounded: it does not poll forever
        Assert.Equal(3, BillingFetches());
        Assert.Equal("Plan_free", cut.Find("[data-testid='billing-plan']").TextContent.Trim());
    }

    [Fact]
    public async Task ReturnFromCheckout_DisposedMidWait_FetchesNothingMore()
    {
        await SignInAsync();
        StubFeatures();
        Http.On(HttpMethod.Get, "/api/billing", Free);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/billing/success");
        var cut = Render<Billing>();
        cut.WaitForElement("[data-testid='billing-plan']");

        await DisposeComponentsAsync(); // the user navigated away while the page was waiting to refetch
        Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(1, BillingFetches());
    }

    // ── TB-UI-53 / 54: only a definite "off" leaves the page ──

    [Fact]
    public async Task BillingOff_LeavesBeforeAnyBillingRequest()
    {
        await SignInAsync();
        StubFeatures(billing: false);

        Render<Billing>();

        Assert.Equal("http://localhost/", Location);
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/billing"));
    }

    [Fact]
    public async Task ProbeUnreachable_DoesNotBounceHome_AndThePageLoads()
    {
        await SignInAsync();
        Http.OnUnreachable(HttpMethod.Get, "/api/features"); // "could not tell", not "off"
        Http.On(HttpMethod.Get, "/api/billing", Free);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/billing");

        var cut = Render<Billing>();

        cut.WaitForElement("[data-testid='billing-plan']");
        Assert.EndsWith("/billing", Location);
    }

    [Fact]
    public async Task ProbeAndApiBothUnreachable_ShowsTheLoadError_NotAHomePage()
    {
        await SignInAsync();
        Http.OnUnreachable(HttpMethod.Get, "/api/features");
        Http.OnUnreachable(HttpMethod.Get, "/api/billing");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/billing");

        var cut = Render<Billing>();

        cut.WaitForAssertion(() => Assert.Contains("Billing_ErrLoad", cut.Find("[data-testid='billing-error']").TextContent));
        Assert.EndsWith("/billing", Location);
    }

    [Fact]
    public void Billing_Anonymous_IsSentToLogin()
    {
        Render<Billing>();

        Assert.EndsWith("/login", Location);
    }

    // ── TB-UI-56..59: the header ──

    private void StaffProbe(bool isStaff) => Http.On(HttpMethod.Get, "/api/admin/me", $$"""{"is_staff":{{(isStaff ? "true" : "false")}}}""");

    [Fact]
    public async Task Header_BillingLink_AppearsAfterAFailedColdProbe_OnceTheServerAnswers()
    {
        StaffProbe(false);
        Http.OnUnreachable(HttpMethod.Get, "/api/features"); // the server was waking up at first render
        await SignInAsync();
        var header = Render<AppHeader>();
        Assert.Empty(header.FindAll("[data-testid=nav-billing]"));

        StubFeatures(billing: true);
        Auth.BeginImpersonation(TestJwt.Build(name: "Someone Else", impersonatedBy: "staff")); // any identity event

        header.WaitForAssertion(() => Assert.NotEmpty(header.FindAll("[data-testid=nav-billing]")));
    }

    [Fact]
    public async Task Header_AdminLink_HidesWhileImpersonating_AndComesBackOnStop()
    {
        StaffProbe(true);
        StubFeatures();
        await SignInAsync(name: "Staff Person");
        var header = Render<AppHeader>();
        header.WaitForAssertion(() => Assert.NotEmpty(header.FindAll("[data-testid=nav-admin]")));

        Auth.BeginImpersonation(TestJwt.Build(name: "Target User", tenantName: "Their Household", impersonatedBy: "staff"));

        header.WaitForAssertion(() => Assert.Empty(header.FindAll("[data-testid=nav-admin]")));
        Assert.Contains("Target User", header.Markup);
        Assert.Equal("Their Household", header.Find("[data-testid=tenant-badge]").TextContent);

        await Auth.StopImpersonationAsync(); // refreshes back into the staff session

        header.WaitForAssertion(() => Assert.NotEmpty(header.FindAll("[data-testid=nav-admin]")));
        Assert.Contains("Staff Person", header.Markup);
    }

    [Fact]
    public async Task Header_IdentityChange_WithTheStaffProbeDown_StillRenders()
    {
        StaffProbe(true);
        StubFeatures();
        await SignInAsync();
        var header = Render<AppHeader>();
        header.WaitForAssertion(() => Assert.NotEmpty(header.FindAll("[data-testid=nav-admin]")));

        Http.OnUnreachable(HttpMethod.Get, "/api/admin/me");
        Auth.BeginImpersonation(TestJwt.Build(name: "Target User", impersonatedBy: "staff"));

        header.WaitForAssertion(() => Assert.Contains("Target User", header.Markup));
        Assert.NotEmpty(header.FindAll("[data-testid=sign-out]"));
    }

    [Fact]
    public async Task Header_Disposed_NoLongerListens_AnIdentityEventDoesNotThrow()
    {
        StaffProbe(false);
        StubFeatures();
        Http.On(HttpMethod.Get, "/api/notifications/unread-count", """{"count":0}""");
        await SignInAsync();
        Render<AppHeader>();
        var probesBefore = Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/admin/me");

        await DisposeComponentsAsync();
        Auth.BeginImpersonation(TestJwt.Build(impersonatedBy: "staff"));

        Assert.Equal(probesBefore, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/admin/me")); // unsubscribed: no re-probe
    }
}

using Bunit;
using Xunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;

namespace Vuelto.Ui.Tests;

/// <summary>
/// GATES-1 (ADR-027), client half: with billing off the link is absent from the header and
/// <c>/billing</c> does not render. This is convenience, not enforcement — the routes are gone from the
/// API either way (<c>BillingGateTests</c>) — but a dead link that 404s is exactly the "there is an
/// upgrade path here" impression the gate exists to remove.
/// </summary>
public class BillingGateUiTests : ComponentTestBase
{
    private void StaffProbe() => Http.On(HttpMethod.Get, "/api/admin/me", "{\"is_staff\":false}");

    [Fact]
    public async Task Header_HidesTheBillingLink_WhenBillingIsOff()
    {
        StubFeatures(billing: false);
        StaffProbe();
        await SignInAsync();

        var header = Render<AppHeader>();

        Assert.Empty(header.FindAll("[data-testid=nav-billing]"));
        Assert.NotEmpty(header.FindAll("[data-testid=sign-out]")); // the rest of the header still rendered
    }

    [Fact]
    public async Task Header_ShowsTheBillingLink_WhenBillingIsOn()
    {
        StubFeatures(billing: true);
        StaffProbe();
        await SignInAsync();

        var header = Render<AppHeader>();

        Assert.NotEmpty(header.FindAll("[data-testid=nav-billing]"));
    }

    [Fact]
    public async Task BillingPage_RedirectsHome_WhenBillingIsOff()
    {
        StubFeatures(billing: false);
        await SignInAsync();

        Render<Billing>();

        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.Equal("http://localhost/", nav.Uri); // bounced off the page, never rendered its summary
    }
}

using Bunit;
using Xunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;

namespace Vuelto.Ui.Tests;

/// <summary>
/// v4 T45 (BILL-3): the staff console's Comp/Revert block follows <c>GET /api/features</c> like the billing link
/// does. With billing off the API answers 404 to both actions, so buttons that offer them are a dead end — and
/// the "nobody holds Pro while off" consequence of ADR-027 would read as broken rather than gated.
/// </summary>
public class AdminConsoleGateUiTests : ComponentTestBase
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private void StubConsole()
    {
        Http.On(HttpMethod.Get, "/api/admin/me", "{\"is_staff\":true}");
        Http.On(HttpMethod.Get, "/api/admin/tenants",
            $"[{{\"id\":\"{TenantId}\",\"name\":\"Smiths\",\"member_count\":2,\"created_at\":\"2026-01-01T00:00:00Z\"}}]");
        Http.On(HttpMethod.Get, $"/api/admin/tenants/{TenantId}",
            $"{{\"id\":\"{TenantId}\",\"name\":\"Smiths\",\"created_at\":\"2026-01-01T00:00:00Z\",\"members\":[],"
            + "\"subscription_status\":\"none\",\"plan_key\":\"free\",\"provider_managed\":false,\"audit_event_count\":0}");
    }

    [Fact]
    public async Task CompButton_IsAbsent_WhenBillingIsOff()
    {
        StubFeatures(billing: false);
        StubConsole();
        await SignInAsync();

        var cut = Render<AdminConsole>();
        cut.WaitForElement("[data-testid=admin-tenant-row]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=admin-plan]"))); // the detail rendered...
        Assert.Empty(cut.FindAll("[data-testid=admin-comp-pro]"));                             // ...without the comp
    }

    [Fact]
    public async Task CompButton_IsOffered_WhenBillingIsOn()
    {
        StubFeatures(billing: true);
        StubConsole();
        await SignInAsync();

        var cut = Render<AdminConsole>();
        cut.WaitForElement("[data-testid=admin-tenant-row]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=admin-comp-pro]")));
    }
}

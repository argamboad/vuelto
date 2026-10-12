using Microsoft.Playwright;

namespace Vuelto.E2E.Tests.Pages;

/// <summary>Page object for the billing page — plan, seats, upgrade/portal actions.</summary>
public class BillingPage(IPage page) : BasePage(page)
{
    public override string Path => "/billing";

    public ILocator Plan => Page.GetByTestId("billing-plan");
    public ILocator Status => Page.GetByTestId("billing-status");
    public ILocator Seats => Page.GetByTestId("billing-seats");
    public ILocator Upgrade => Page.GetByTestId("billing-upgrade");
    public ILocator Portal => Page.GetByTestId("billing-portal");
    public ILocator OwnerOnly => Page.GetByTestId("billing-owner-only");
}

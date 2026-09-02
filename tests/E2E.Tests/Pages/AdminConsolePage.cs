using Microsoft.Playwright;

namespace Perezosoft.E2E.Tests.Pages;

/// <summary>Page object for the staff-only admin console (/admin).</summary>
public class AdminConsolePage(IPage page) : BasePage(page)
{
    public override string Path => "/admin";

    public ILocator TenantRows => Page.GetByTestId("admin-tenant-row");
    public ILocator TenantRow(string name) => TenantRows.Filter(new() { HasText = name });
    public ILocator AnnounceTitle => Page.GetByTestId("admin-announce-title");
    public ILocator AnnounceBody => Page.GetByTestId("admin-announce-body");
    public ILocator AnnounceSend => Page.GetByTestId("admin-announce-send");
    public ILocator Status => Page.GetByTestId("admin-status");
    public ILocator Forbidden => Page.GetByTestId("admin-forbidden");

    /// <summary>Fills and sends the announcement form (caller registers the confirm-dialog handler).</summary>
    public async Task AnnounceAsync(string title, string body)
    {
        await AnnounceTitle.FillAsync(title);
        await AnnounceBody.FillAsync(body);
        // Blazor's @bind updates on the change event — blur so the send button enables.
        await AnnounceBody.BlurAsync();
        await AnnounceSend.ClickAsync();
    }
}

using Microsoft.Playwright;

namespace Vuelto.E2E.Tests.Pages;

/// <summary>Page object for the invitation-accept page (/join?token=… or manual code entry).</summary>
public class JoinPage(IPage page) : BasePage(page)
{
    public override string Path => "/join";

    public ILocator Success => Page.GetByTestId("join-success");
    public ILocator GoToHousehold => Page.GetByTestId("join-go-household");
    public ILocator NeedsSignIn => Page.GetByTestId("join-needs-signin");
    public ILocator Error => Page.GetByTestId("join-error");
    public ILocator HouseholdFull => Page.GetByTestId("join-household-full");

    public ILocator CodeInput => Page.GetByTestId("join-code-input");
    public ILocator CodeSubmit => Page.GetByTestId("join-code-submit");
    public ILocator CodeError => Page.GetByTestId("join-code-error");

    public Task GotoWithTokenAsync(string token) =>
        Page.GotoAsync($"{Path}?token={Uri.EscapeDataString(token)}");

    /// <summary>The native path (parity gap G5): open bare /join and paste the emailed code.</summary>
    public async Task JoinWithCodeAsync(string token)
    {
        await GotoAsync();
        await CodeInput.FillAsync(token);
        await CodeSubmit.ClickAsync();
    }
}

using Microsoft.Playwright;

namespace Vuelto.E2E.Tests.Pages;

/// <summary>Page object for the Household page — roster, role badges, and invitations.</summary>
public class HouseholdPage(IPage page) : BasePage(page)
{
    public override string Path => "/household";

    public ILocator RenameInput => Page.GetByTestId("household-rename-input");
    public ILocator RenameSave => Page.GetByTestId("household-rename-save");
    public ILocator TransferSelect => Page.GetByTestId("transfer-select");
    public ILocator TransferSubmit => Page.GetByTestId("transfer-submit");
    public ILocator LeaveDissolve => Page.GetByTestId("leave-dissolve");
    public ILocator LeaveHousehold => Page.GetByTestId("leave-household");
    public ILocator InviteEmail => Page.GetByTestId("invite-email");
    public ILocator InviteSend => Page.GetByTestId("invite-send");
    public ILocator RevealedToken => Page.GetByTestId("invite-token");
    public ILocator Status => Page.GetByTestId("household-status");
    public ILocator ExportData => Page.GetByTestId("export-data");
    public ILocator ExportDownload => Page.GetByTestId("export-download");
    public ILocator MemberRows => Page.GetByTestId("member-row");
    public ILocator PendingRows => Page.GetByTestId("pending-invite-row");

    public ILocator MemberRow(string email) => MemberRows.Filter(new() { HasText = email });
    public ILocator RoleBadge(string email) => MemberRow(email).GetByTestId("member-role");
    public ILocator Promote(string email) => MemberRow(email).GetByTestId("member-promote");
    public ILocator Demote(string email) => MemberRow(email).GetByTestId("member-demote");
    public ILocator Remove(string email) => MemberRow(email).GetByTestId("member-remove");
    public ILocator PendingRow(string email) => PendingRows.Filter(new() { HasText = email });
    public ILocator Revoke(string email) => PendingRow(email).GetByTestId("invite-revoke");

    /// <summary>Renames the household through the UI.</summary>
    public async Task RenameAsync(string name)
    {
        await RenameInput.FillAsync(name);
        // Blazor's @bind updates on the change event — blur so the save button enables.
        await RenameInput.BlurAsync();
        await RenameSave.ClickAsync();
    }

    /// <summary>Sends an invitation and returns the one-time token the UI reveals.</summary>
    public async Task<string> InviteAsync(string email)
    {
        await InviteEmail.FillAsync(email);
        // Blazor's @bind updates on the change event — blur so the send button enables.
        await InviteEmail.BlurAsync();
        await InviteSend.ClickAsync();
        await Assertions.Expect(RevealedToken).ToBeVisibleAsync(new() { Timeout = 15_000 });
        return (await RevealedToken.InnerTextAsync()).Trim();
    }
}

using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>EMAIL-2/3 UI: lists connections with status, connect navigates to the consent URL, edit loads folders and PUTs the settings, disconnect is two-step, and the callback query shows feedback.</summary>
public class EmailSettingsPageTests : ComponentTestBase
{
    private const string C1 = "eeeeeeee-0000-0000-0000-000000000001";
    private const string C2 = "eeeeeeee-0000-0000-0000-000000000002";
    private const string List = $$"""
        [{"id":"{{C1}}","provider":"microsoft","account_email":"me@outlook.com","status":"active","folders":[{"id":"id-inbox","name":"Inbox"}],"sender_filters":["notificacion@notificacionesbaccr.com"],"subject_filters":["Notificación de transacción","Voucher Digital"],"unread_only":true,"ignore_cursor":false,"import_from":"2026-06-16T12:00:00+00:00","polling_interval_minutes":15,"last_polled_at":"2026-06-16T12:00:00+00:00","created_at":"2026-06-16T12:00:00+00:00"},
         {"id":"{{C2}}","provider":"google","account_email":"me@gmail.com","status":"needs_reconsent","folders":[],"sender_filters":[],"subject_filters":["Voucher Digital"],"unread_only":true,"ignore_cursor":false,"import_from":"2026-06-16T12:00:00+00:00","polling_interval_minutes":30,"last_polled_at":null,"created_at":"2026-06-16T12:00:00+00:00"}]
        """;

    [Fact]
    public async Task Lists_Connections_WithProviderAccountStatus_AndReconnectForDeadOnes()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/email/connections", List);

        var cut = Render<EmailSettings>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='email-connection']").Count));
        var rows = cut.FindAll("[data-testid='email-connection']");
        Assert.Contains("Email_Outlook", rows[0].TextContent); Assert.Contains("me@outlook.com", rows[0].TextContent); Assert.Contains("Email_Active", rows[0].TextContent);
        Assert.Contains("Email_NeedsReconnect", rows[1].TextContent); Assert.Contains("Email_Never", rows[1].TextContent);
        Assert.Single(cut.FindAll("[data-testid='email-reconnect']"));
    }

    [Fact]
    public async Task Connect_IsDisabled_ForAProviderThatIsAlreadyConnected()
    {
        await SignInAsync();
        // Only Outlook connected: its Connect button greys out, Gmail's stays live.
        Http.On(HttpMethod.Get, "/api/email/connections", List.Split("},")[0] + "}]"); // the first row of List, alone

        var cut = Render<EmailSettings>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='email-connection']")));

        Assert.True(cut.Find("[data-testid='email-connect-microsoft']").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid='email-connect-google']").HasAttribute("disabled"));

        // Both connected (Gmail dead, awaiting reconsent): both Connect buttons grey out; Reconnect on the row is the way back.
        Http.On(HttpMethod.Get, "/api/email/connections", List);
        cut = Render<EmailSettings>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='email-connection']").Count));

        Assert.True(cut.Find("[data-testid='email-connect-microsoft']").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid='email-connect-google']").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid='email-reconnect']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Connect_NavigatesToTheConsentUrl_OrExplainsAnUnconfiguredProvider()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/email/connections", "[]");
        Http.On(HttpMethod.Get, "/api/email/connections/authorize", """{"authorization_url":"https://accounts.google.com/o/oauth2/v2/auth?client_id=g"}""");

        var cut = Render<EmailSettings>();
        cut.WaitForElement("[data-testid='email-empty']");
        cut.Find("[data-testid='email-connect-google']").Click();

        cut.WaitForAssertion(() => Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth?client_id=g", Services.GetRequiredService<NavigationManager>().Uri));
        Assert.Contains("provider=google", Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/email/connections/authorize").RequestUri!.Query);

        Http.On(HttpMethod.Get, "/api/email/connections/authorize", """{"error":"provider_not_configured","message":"x"}""", HttpStatusCode.BadRequest);
        cut.Find("[data-testid='email-connect-microsoft']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Email_NotConfigured[Email_Outlook]", cut.Find("[data-testid='email-notice']").TextContent));
    }

    [Fact]
    public async Task Edit_LoadsFolders_AndPutsTheSettings()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/email/connections", List);
        Http.On(HttpMethod.Get, $"/api/email/connections/{C1}/folders", """[{"id":"id-inbox","name":"Inbox"},{"id":"id-vouchers","name":"Inbox/Vouchers"}]""");
        Http.On(HttpMethod.Put, $"/api/email/connections/{C1}", """{"id":"x"}""");

        var cut = Render<EmailSettings>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='email-edit']").Count));
        cut.FindAll("[data-testid='email-edit']")[0].Click();
        Assert.Equal("Inbox", Assert.Single(cut.FindAll("[data-testid='email-folder-chip']")).TextContent.Trim());
        cut.Find("[data-testid='email-load-folders']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='email-folder']").Count));
        Assert.True(cut.FindAll("[data-testid='email-folder']")[0].HasAttribute("checked"));
        cut.FindAll("[data-testid='email-folder']")[1].Change(true);
        cut.Find("[data-testid='email-folders-apply']").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='email-folder-picker']")));
        Assert.Equal(["Inbox", "Inbox/Vouchers"], cut.FindAll("[data-testid='email-folder-chip']").Select(e => e.TextContent.Trim()));
        cut.Find("[data-testid='email-subjects']").Change("Voucher Digital, BN Conectividad le informa");
        cut.Find("[data-testid='email-interval']").Change("60");
        cut.Find("[data-testid='email-ignore-cursor']").Change(true);
        cut.Find("[data-testid='email-save']").Click();

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put));
        var body = await Http.Requests.Single(r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Contains("\"folders\":[{\"id\":\"id-inbox\",\"name\":\"Inbox\"},{\"id\":\"id-vouchers\",\"name\":\"Inbox/Vouchers\"}]", body);
        Assert.Contains("\"subject_filters\":[\"Voucher Digital\",\"BN Conectividad le informa\"]", body);
        Assert.Contains("\"polling_interval_minutes\":60", body);
        Assert.Contains("\"ignore_cursor\":true", body);
        Assert.Contains("\"unread_only\":true", body);
        Assert.Contains("\"import_from\":null", body); // untouched date is not re-sent (it would pull the cursor back)
        Assert.Equal(2, Http.Requests.Count(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/email/connections")); // reloaded
    }

    [Fact]
    public async Task Folders_RowShowsTheNames_AndCancelDropsTheDraft()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/email/connections", List);
        Http.On(HttpMethod.Get, $"/api/email/connections/{C1}/folders", """[{"id":"id-inbox","name":"Inbox"},{"id":"id-vouchers","name":"Inbox/Vouchers"}]""");

        var cut = Render<EmailSettings>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='email-folders-row']").Count));
        // The collapsed row already says what is scanned — names, or the inbox default.
        var rows = cut.FindAll("[data-testid='email-folders-row']");
        Assert.Equal("Email_FoldersRow[Inbox]", rows[0].TextContent.Trim());
        Assert.Equal("Email_FoldersRow[Email_InboxDefault]", rows[1].TextContent.Trim());

        // A name the server could not resolve (dead token) shows the placeholder, never the opaque id.
        Http.On(HttpMethod.Get, "/api/email/connections", List.Replace("""{"id":"id-inbox","name":"Inbox"}""", """{"id":"AQMkADAwATIwMTAw","name":null}"""));
        var unnamed = Render<EmailSettings>();
        unnamed.WaitForAssertion(() => Assert.Equal(2, unnamed.FindAll("[data-testid='email-folders-row']").Count));
        Assert.Equal("Email_FoldersRow[Email_FolderUnnamed]", unnamed.FindAll("[data-testid='email-folders-row']")[0].TextContent.Trim());
        unnamed.FindAll("[data-testid='email-edit']")[0].Click();
        Assert.Equal("Email_FolderUnnamed", Assert.Single(unnamed.FindAll("[data-testid='email-folder-chip']")).TextContent.Trim());

        cut.FindAll("[data-testid='email-edit']")[0].Click();
        cut.Find("[data-testid='email-load-folders']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='email-folder']").Count));
        Assert.Empty(cut.FindAll("[data-testid='email-load-folders']")); // the link hides while the list is open
        cut.FindAll("[data-testid='email-folder']")[1].Change(true);
        cut.Find("[data-testid='email-folders-cancel']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='email-folder-picker']")));
        Assert.Equal("Inbox", Assert.Single(cut.FindAll("[data-testid='email-folder-chip']")).TextContent.Trim()); // the toggle was dropped
        Assert.Single(cut.FindAll("[data-testid='email-load-folders']"));
    }

    [Fact]
    public async Task Edit_ChangingImportFrom_SendsTheNewDate()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/email/connections", List);
        Http.On(HttpMethod.Put, $"/api/email/connections/{C1}", """{"id":"x"}""");

        var cut = Render<EmailSettings>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='email-edit']").Count));
        cut.FindAll("[data-testid='email-edit']")[0].Click();
        cut.Find("[data-testid='email-import-from']").Change("2026-06-01");
        cut.Find("[data-testid='email-save']").Click();

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put));
        var body = await Http.Requests.Single(r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Contains("\"import_from\":\"2026-06-01T00:00:00+00:00\"", body);
    }

    [Fact]
    public async Task SyncNow_PostsTheSync_AndShowsTheCounts_OrTheReconnectMessage()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/email/connections", List);
        Http.On(HttpMethod.Post, $"/api/email/connections/{C1}/sync", """{"staged":2,"duplicates":1,"unrecognized":3}""");
        Http.On(HttpMethod.Post, $"/api/email/connections/{C2}/sync", """{"error":"needs_reconsent","message":"x"}""", HttpStatusCode.Conflict);

        var cut = Render<EmailSettings>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='email-sync']").Count));
        cut.FindAll("[data-testid='email-sync']")[0].Click();
        cut.WaitForAssertion(() => Assert.Contains("Email_SyncResult[2, 1, 3]", cut.Find("[data-testid='email-notice']").TextContent));
        Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == $"/api/email/connections/{C1}/sync");

        cut.FindAll("[data-testid='email-sync']")[1].Click();
        cut.WaitForAssertion(() => Assert.Contains("Email_ReconnectToSync", cut.Find("[data-testid='email-notice']").TextContent));
    }

    [Fact]
    public async Task Disconnect_IsTwoStep_ThenDeletesAndReloads()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/email/connections", List);
        Http.On(HttpMethod.Delete, $"/api/email/connections/{C1}", "", HttpStatusCode.NoContent);

        var cut = Render<EmailSettings>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='email-disconnect']").Count));
        cut.FindAll("[data-testid='email-disconnect']")[0].Click();
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Delete);
        cut.Find("[data-testid='email-disconnect-confirm']").Click();

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath == $"/api/email/connections/{C1}"));
        Assert.Contains("Email_Disconnected", cut.Find("[data-testid='email-notice']").TextContent);
    }

    [Fact]
    public async Task CallbackQuery_ShowsSuccessOrTheError()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/email/connections", "[]");
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/email?connected=google");
        var cut = Render<EmailSettings>();
        cut.WaitForAssertion(() => Assert.Contains("Email_ConnectSuccess[Email_Gmail]", cut.Find("[data-testid='email-notice']").TextContent));

        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/email?email_error=already_connected");
        var cut2 = Render<EmailSettings>();
        cut2.WaitForAssertion(() => Assert.Contains("Email_ConnectErrorExists", cut2.Find("[data-testid='email-notice']").TextContent));
    }
}

using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// SKIN-11: the fourteen screens the handout never drew, conformed to the system the other ten build. Two shared
/// pieces (the page head and the status chip), the consequence panels on the destructive actions, and the phone
/// sub-lines where a table folds its columns.
/// </summary>
public class SweepTests : ComponentTestBase
{
    private const string Me = "11111111-1111-1111-1111-111111111111"; // TestJwt's default subject
    private const string Other = "33333333-3333-3333-3333-333333333333";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";

    // ---------------------------------------------------------------- the shared pieces

    [Fact]
    public void PageHead_RendersTitleSubtitleAndActions()
    {
        var cut = Render<PageHead>(p => p.Add(x => x.Title, "Cards").Add(x => x.Subtitle, "What you pay with")
            .AddChildContent("<button data-testid='act'>New</button>"));

        Assert.Equal("Cards", cut.Find("h1").TextContent);
        Assert.Equal("What you pay with", cut.Find("[data-testid='page-subtitle']").TextContent);
        Assert.NotNull(cut.Find("[data-testid='page-head-actions'] [data-testid='act']"));

        var bare = Render<PageHead>(p => p.Add(x => x.Title, "Cards"));
        Assert.Empty(bare.FindAll("[data-testid='page-subtitle']"));
        Assert.Empty(bare.FindAll("[data-testid='page-head-actions']")); // no empty actions slot
    }

    [Fact]
    public void StatusChip_CarriesItsToneAsData_AndPassesExtraAttributesThrough()
    {
        var cut = Render<StatusChip>(p => p.Add(x => x.Tone, "good").Add(x => x.TestId, "s").AddUnmatched("data-role", "owner").AddChildContent("Active"));
        var chip = cut.Find("[data-testid='s']");
        Assert.Equal("good", chip.GetAttribute("data-tone"));
        Assert.Equal("owner", chip.GetAttribute("data-role"));
        Assert.Equal("Active", chip.TextContent.Trim());
        Assert.Equal("neutral", Render<StatusChip>(p => p.AddChildContent("x")).Find(".status-chip").GetAttribute("data-tone"));
    }

    [Fact]
    public void CurrencySwitch_IsTheSegmentedTrack_AndSaysWhichOptionIsOn()
    {
        var cut = Render<CurrencySwitch>(p => p.Add(x => x.Value, MoneyDisplay.Usd).Add(x => x.TestId, "c").Add(x => x.Label, "Show in"));
        Assert.Equal("true", cut.Find("[data-testid='c-usd']").GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.Find("[data-testid='c-crc']").GetAttribute("aria-pressed"));
        Assert.Empty(cut.FindAll(".btn-group")); // the old outlined button group is gone
        var picked = "";
        var wired = Render<CurrencySwitch>(p => p.Add(x => x.Value, MoneyDisplay.Both).Add(x => x.TestId, "c").Add(x => x.ValueChanged, v => picked = v));
        wired.Find("[data-testid='c-crc']").Click();
        Assert.Equal(MoneyDisplay.Crc, picked);
    }

    // ---------------------------------------------------------------- consequence panels on the destructive actions

    private static string Household(string myRole, bool withOthers) => $$"""
        {"id":"{{TenantId}}","name":"Casa Gamboa","my_role":"{{myRole}}","members":[
          {"user_id":"{{Me}}","display_name":"Ada","email":"ada@example.com","role":"{{myRole}}","joined_at":"2026-01-01T00:00:00+00:00"}
          {{(withOthers ? $$""",{"user_id":"{{Other}}","display_name":"Bob","email":"bob@example.com","role":"member","joined_at":"2026-02-01T00:00:00+00:00"}""" : "")}}
        ]}
        """;

    [Fact]
    public async Task Household_SoleOwner_StatesWhatDissolvingDoes_BeforeTheButton()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/household", Household("owner", withOthers: false));
        Http.On(HttpMethod.Get, "/api/household/invitations", "[]");

        var cut = Render<Vuelto.Shared.Ui.Pages.Household>();
        var panel = cut.WaitForElement("[data-testid='consequence-dissolve']");
        Assert.Contains("Household_DissolveWarning", panel.QuerySelector("[data-testid='consequence-dissolve-body']")!.TextContent);
        Assert.NotNull(panel.QuerySelector("[data-testid='consequence-dissolve-actions'] [data-testid='leave-dissolve']")); // the body precedes the button
        Assert.Empty(cut.FindAll("[data-testid='consequence-leave']"));
        Assert.Equal("plan", cut.Find("[data-testid='member-role']").GetAttribute("data-tone")); // the owner's chip is the brand tint, not a Bootstrap badge
        Assert.Equal("owner", cut.Find("[data-testid='member-role']").GetAttribute("data-role"));
    }

    [Fact]
    public async Task Household_Member_StatesWhatLeavingDoes_AndOwnerWithMembers_WhatTransferringDoes()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/household", Household("member", withOthers: true));
        var member = Render<Vuelto.Shared.Ui.Pages.Household>();
        var leave = member.WaitForElement("[data-testid='consequence-leave']");
        Assert.Contains("Household_LeaveAsMemberNote", leave.QuerySelector("[data-testid='consequence-leave-body']")!.TextContent);
        Assert.NotNull(leave.QuerySelector("[data-testid='consequence-leave-actions'] [data-testid='leave-household']"));

        Http.On(HttpMethod.Get, "/api/household", Household("owner", withOthers: true));
        Http.On(HttpMethod.Get, "/api/household/invitations", "[]");
        var owner = Render<Vuelto.Shared.Ui.Pages.Household>();
        var transfer = owner.WaitForElement("[data-testid='consequence-transfer']");
        Assert.Contains("Household_TransferConsequence", transfer.QuerySelector("[data-testid='consequence-transfer-body']")!.TextContent);
        Assert.NotNull(transfer.QuerySelector("[data-testid='consequence-transfer-actions'] [data-testid='transfer-submit']"));
        Assert.Empty(owner.FindAll("[data-testid='consequence-dissolve']"));
    }

    [Fact]
    public async Task Settings_StatesWhatDeletingTheAccountDoes_BeforeTheButton()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/auth/providers", """{"providers":["google"]}""");
        Http.On(HttpMethod.Get, "/api/auth/logins", """[{"provider":"google"}]""");

        var cut = Render<Settings>();
        var panel = cut.WaitForElement("[data-testid='consequence-delete-account']");
        Assert.Contains("Settings_DeleteAccountWarning", panel.QuerySelector("[data-testid='consequence-delete-account-body']")!.TextContent);
        Assert.NotNull(panel.QuerySelector("[data-testid='consequence-delete-account-actions'] [data-testid='delete-account']"));
        Assert.Equal("good", cut.WaitForElement("[data-testid='settings-linked']").GetAttribute("data-tone")); // "Connected" is a chip now
    }

    [Fact]
    public async Task Admin_StatesWhatCompingAndRevertingDo_BeforeTheButtons()
    {
        const string T = "44444444-4444-4444-4444-444444444444";
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/admin/me", """{"is_staff":true}""");
        Http.On(HttpMethod.Get, "/api/admin/tenants", $$"""[{"id":"{{T}}","name":"Casa Gamboa","member_count":2,"created_at":"2026-01-01T00:00:00+00:00"}]""");
        Http.On(HttpMethod.Get, $"/api/admin/tenants/{T}", $$"""{"id":"{{T}}","name":"Casa Gamboa","created_at":"2026-01-01T00:00:00+00:00","members":[{"user_id":"{{Other}}","display_name":"Bob","email":"bob@example.com","role":"owner"}],"subscription_status":"active","plan_key":"free","provider_managed":false,"audit_event_count":3}""");

        var cut = Render<AdminConsole>();
        cut.WaitForElement("[data-testid='admin-tenant-row']").Click();

        var panel = cut.WaitForElement("[data-testid='consequence-plan']");
        Assert.Contains("Admin_CompConsequence", panel.QuerySelector("[data-testid='consequence-plan-body']")!.TextContent);
        Assert.NotNull(panel.QuerySelector("[data-testid='consequence-plan-actions'] [data-testid='admin-comp-pro']"));
        Assert.Equal("plan", cut.Find("[data-testid='admin-plan']").GetAttribute("data-tone"));
        Assert.Contains("owner", cut.Find("[data-testid='admin-member-sub']").TextContent); // the role folds under the name on phone
        Assert.Contains("Bob", cut.Find("[data-testid='admin-member-check']").GetAttribute("aria-label")); // a bare checkbox names its member

        // On Pro the panel says what reverting does instead.
        Http.On(HttpMethod.Get, $"/api/admin/tenants/{T}", $$"""{"id":"{{T}}","name":"Casa Gamboa","created_at":"2026-01-01T00:00:00+00:00","members":[],"subscription_status":"active","plan_key":"pro","provider_managed":false,"audit_event_count":3}""");
        cut.Find("[data-testid='admin-tenant-row']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Admin_RevertConsequence", cut.Find("[data-testid='consequence-plan-body']").TextContent));
        Assert.NotNull(cut.Find("[data-testid='consequence-plan-actions'] [data-testid='admin-revert-free']"));
    }

    // ---------------------------------------------------------------- tables that fold on phone

    [Fact]
    public async Task Envelopes_StackTheTwoTargets_AndFoldCadenceAndStatusUnderTheName()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/envelopes", """[{"id":"aaaaaaaa-0000-0000-0000-000000000001","name":"Marchamo","annual_target_crc":718000.00,"annual_target_usd":120,"reminder_cadence":"five_week_months","is_active":true}]""");

        var cut = Render<Envelopes>();
        var row = cut.WaitForElement("[data-testid='env-row']");
        Assert.Equal("₡718,000.00", row.QuerySelector("[data-testid='env-row-target-primary']")!.TextContent);
        Assert.Equal("$120.00", row.QuerySelector("[data-testid='env-row-target-secondary']")!.TextContent);
        Assert.Empty(cut.FindAll("[data-testid='env-row-crc']")); // no longer two columns
        Assert.Equal("Envelopes_CadenceFiveWeek · Catalog_Active", row.QuerySelector("[data-testid='env-row-sub']")!.TextContent.Trim());
        Assert.Equal("good", row.QuerySelector("[data-testid='env-row-status']")!.GetAttribute("data-tone"));
    }

    [Fact]
    public async Task Cards_FoldIdentityKindAndBankUnderTheAlias()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/banks", """[{"id":"bbbbbbbb-0000-0000-0000-000000000001","name":"BAC Credomatic","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/cards", """[{"id":"aaaaaaaa-0000-0000-0000-000000000001","name":"VISA-1234","brand":"VISA","last4":"1234","bank_id":"bbbbbbbb-0000-0000-0000-000000000001","is_active":true,"auto_named":true,"kind":"credit"}]""");

        var cut = Render<Cards>();
        var row = cut.WaitForElement("[data-testid='cards-row']");
        Assert.Contains("VISA ····1234", row.QuerySelector("[data-testid='cards-row-sub']")!.TextContent);
        Assert.Contains("BAC Credomatic", row.QuerySelector("[data-testid='cards-row-sub']")!.TextContent);
        Assert.Equal("warn", row.QuerySelector("[data-testid='cards-auto']")!.GetAttribute("data-tone")); // auto-named asks for attention, amber
        Assert.Equal("good", row.QuerySelector("[data-testid='cards-row-status']")!.GetAttribute("data-tone"));
    }

    [Fact]
    public async Task MerchantMappings_FoldCategoryAndClassUnderThePattern()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", """[{"id":"bbbbbbbb-0000-0000-0000-000000000002","name":"Groceries","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/merchant-mappings", """[{"id":"aaaaaaaa-0000-0000-0000-000000000001","merchant_pattern":"automercado","category_id":"bbbbbbbb-0000-0000-0000-000000000002","category_name":"Groceries","suggested_class":"budgeted"}]""");

        var cut = Render<MerchantMappings>();
        var row = cut.WaitForElement("[data-testid='mapping-row']");
        Assert.Contains("Groceries", row.QuerySelector("[data-testid='mapping-row-sub']")!.TextContent);
        Assert.NotNull(row.QuerySelector("[data-testid='mapping-row-class'] .class-chip")); // the class is the chip everywhere
    }
}

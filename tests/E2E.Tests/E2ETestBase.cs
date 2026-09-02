using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;
using Vuelto.E2E.Tests.Pages;

namespace Vuelto.E2E.Tests;

/// <summary>
/// Base for E2E tests. Drives a real browser against the running Web app.
/// <para>
/// Prereqs (see docs/WAYS_OF_WORKING.md): <c>docker compose up -d</c>, then start the API
/// (https profile) and the Web app, then run. Base URL defaults to the Web app's https
/// profile and is overridable via <c>PLAYWRIGHT_BASE_URL</c>.
/// </para>
/// </summary>
public abstract class E2ETestBase : PageTest
{
    // Resolution order: the runsettings TestRunParameter wins (so `-- TestRunParameters...` /
    // playwright.runsettings actually take effect), then the environment variable, then the Web
    // app's https launch profile (https://localhost:7008).
    protected static string BaseUrl =>
        TestContext.Parameters.Get("PLAYWRIGHT_BASE_URL")
        ?? Environment.GetEnvironmentVariable("PLAYWRIGHT_BASE_URL")
        ?? "https://localhost:7008";

    // The API's own origin, for tests that simulate an external caller (e.g. the billing-provider
    // webhook). Defaults to the API's https launch profile; CI overrides to its single-origin base.
    protected static string ApiBaseUrl =>
        TestContext.Parameters.Get("E2E_API_BASE_URL")
        ?? Environment.GetEnvironmentVariable("E2E_API_BASE_URL")
        ?? "https://localhost:7160";

    /// <summary>HttpClient for API calls made by tests themselves (accepts the dev self-signed cert).</summary>
    protected static HttpClient NewApiClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { BaseAddress = new Uri(ApiBaseUrl) };

    /// <summary>
    /// POSTs the billing-provider webhook exactly as Stripe would, accepted by the
    /// FakeBillingProvider (the E2E API runs without a Stripe key — README): PascalCase body
    /// (BillingWebhookEvent uses DEFAULT System.Text.Json options, so don't serialize camelCase)
    /// + the fake's always-valid signature. Lets tests drive plan changes — upgrades AND
    /// downgrades — with no Stripe. Pass a strictly-later <paramref name="occurredAt"/> for a
    /// follow-up event: the projection applies only newer events (R29).
    /// </summary>
    protected static async Task PostBillingWebhookAsync(string tenantId, string status, string planKey = "pro",
        DateTimeOffset? occurredAt = null)
    {
        using var api = NewApiClient();
        var payload = JsonSerializer.Serialize(new
        {
            EventId = $"evt_e2e_{Guid.NewGuid():N}",
            TenantId = tenantId,
            PlanKey = planKey,
            Status = status,
            StripeCustomerId = "cus_e2e",
            StripeSubscriptionId = "sub_e2e",
            CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        });
        var webhook = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        webhook.Headers.Add("Stripe-Signature", "valid"); // FakeBillingProvider.ValidSignature
        var response = await api.SendAsync(webhook);
        Assert.That(response.IsSuccessStatusCode, Is.True, $"webhook returned {(int)response.StatusCode}");
    }

    // Dev runs on a self-signed cert, so ignore HTTPS errors for the test browser.
    public override BrowserNewContextOptions ContextOptions() => new()
    {
        BaseURL = BaseUrl,
        IgnoreHTTPSErrors = true,
    };

    /// <summary>OTP sign-in on the given page/context and wait for the app shell.</summary>
    protected static async Task SignInAsync(IPage page, string email)
    {
        var login = new LoginPage(page);
        await login.GotoAsync();
        await Assertions.Expect(login.Email).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await login.SignInWithOtpAsync(email);
        await Assertions.Expect(page.GetByTestId("sign-out")).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    /// <summary>Clears Mailpit, signs the user in, and lands on the Household page.</summary>
    protected static async Task<HouseholdPage> SignInToHouseholdAsync(IPage page, string email)
    {
        await Mailpit.ClearAsync();
        await SignInAsync(page, email);
        var household = new HouseholdPage(page);
        await household.GotoAsync();
        return household;
    }

    /// <summary>
    /// Owner invites <paramref name="memberEmail"/>; the member signs in on
    /// <paramref name="memberPage"/> and accepts via the revealed token.
    /// </summary>
    protected static async Task InviteAndJoinAsync(HouseholdPage owner, IPage memberPage, string memberEmail)
    {
        var token = await owner.InviteAsync(memberEmail);

        // Drop the invitation email so the member's OTP poll can't misread it.
        await Mailpit.ClearAsync();
        await SignInAsync(memberPage, memberEmail);

        var join = new JoinPage(memberPage);
        await join.GotoWithTokenAsync(token);
        await Assertions.Expect(join.Success).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    protected static string UniqueEmail(string role) => $"e2e-{role}-{Guid.NewGuid():N}@example.com";
}

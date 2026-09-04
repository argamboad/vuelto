using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Vuelto.Shared.Ui;
using Vuelto.Shared.Ui.Resources;
using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Ui.Tests.Infrastructure;

/// <summary>
/// Base for RCL component tests (v3 TOOL-2). Registers a test double for every seam the shared components
/// inject, so a real component renders against a controllable environment: <see cref="AuthService"/> over a
/// <see cref="TestHttpHandler"/> + <see cref="FakeSessionStore"/>, bUnit's fake NavigationManager + JSInterop,
/// a deterministic localizer, and in-memory theme/culture stores. <see cref="SignInAsync"/> drives the REAL
/// refresh path (no reflection) to put AuthService into a signed-in state.
/// </summary>
public abstract class ComponentTestBase : BunitContext
{
    protected TestHttpHandler Http { get; } = new();
    protected FakeThemePersistence ThemeStore { get; } = new();
    protected FakeCulturePersistence CultureStore { get; } = new();
    protected FakeFileDownloadLauncher Downloads { get; } = new();
    protected AuthService Auth { get; }

    protected ComponentTestBase()
    {
        // Deterministic per test: MainLayout's locale reconcile mutates the process-global
        // CultureInfo.CurrentUICulture, which would otherwise leak into the next test (xUnit runs a class's
        // tests in one process). Reset to English so culture-sensitive components start from a known state.
        System.Globalization.CultureInfo.CurrentCulture =
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en");

        var sessionStore = new FakeSessionStore();
        Auth = new AuthService(
            new HttpClient(Http) { BaseAddress = new Uri("http://localhost") },
            NullLogger<AuthService>.Instance,
            sessionStore);

        Services.AddSingleton(Auth);
        Services.AddSingleton(new HttpClient(Http) { BaseAddress = new Uri("http://localhost") });
        Services.AddSingleton<ISessionStore>(sessionStore);
        Services.AddSingleton<IThemePersistence>(ThemeStore);
        Services.AddSingleton<ICulturePersistence>(CultureStore);
        Services.AddSingleton<IFileDownloadLauncher>(Downloads); // pages with a download (Household export, Reports CSV) inject it
        Services.AddSingleton<IStringLocalizer<AppStrings>>(new FakeStringLocalizer());
        Services.AddSingleton<AppResumeNotifier>(); // pages that refresh on app-resume (Billing) inject it
        Services.AddSingleton<ReviewQueueNotifier>(); // the header badge + the Review page (EMAIL-6)

        // bUnit ships a fake NavigationManager (assert via Services.GetRequiredService<NavigationManager>())
        // and a JSInterop (JSInterop.Mode = Loose so unmatched JS calls no-op rather than throw).
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Put <see cref="Auth"/> into a signed-in state by driving the actual refresh flow: stub
    /// POST /api/auth/refresh to return an access token carrying the given claims, then InitializeAsync.
    /// Higher fidelity than reflecting the private field — the same code path a cold start uses.
    /// </summary>
    protected async Task SignInAsync(
        string? name = "Ada Lovelace",
        string? tenantName = "Test Household",
        string? locale = null,
        string? theme = null,
        string? impersonatedBy = null)
    {
        var jwt = TestJwt.Build(name: name, tenantName: tenantName, locale: locale, theme: theme,
            impersonatedBy: impersonatedBy);
        Http.On(HttpMethod.Post, "/api/auth/refresh", $"{{\"access_token\":\"{jwt}\"}}");
        await Auth.InitializeAsync();
    }
}

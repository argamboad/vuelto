using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using Perezosoft.Shared.Ui;
using Perezosoft.Shared.Ui.Auth;
using Perezosoft.Web.Http;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Same-origin by default (DEPLOY-1, ADR-017): when the API serves this bundle (single-origin
// deployment), the API lives on this app's own origin, so fall back to the host base address. An
// explicit ApiBaseUrl (local dev's wwwroot/appsettings.json, the e2e CI override) still takes precedence.
var apiBase = builder.Configuration["ApiBaseUrl"] is { Length: > 0 } configured
    ? configured
    : builder.HostEnvironment.BaseAddress;

// Delegating handlers:
//  - CookieHandler includes browser credentials (the HttpOnly refresh cookie).
//  - AuthHeaderHandler attaches the in-memory JWT as a Bearer header.
// Use IHttpClientFactory so the framework supplies the browser HTTP handler as the
// primary handler (you cannot `new HttpClientHandler()` in Blazor WASM).
builder.Services.AddScoped<CookieHandler>();
builder.Services.AddScoped<AuthHeaderHandler>();

// "Api" — the general-purpose client used by components: credentials + Bearer token.
builder.Services.AddHttpClient("Api", client => client.BaseAddress = new Uri(apiBase))
    .AddHttpMessageHandler<CookieHandler>()
    .AddHttpMessageHandler<AuthHeaderHandler>();

// "ApiAuth" — used only by AuthService for refresh/logout. Credentials only (no
// Bearer handler) to avoid a DI cycle (AuthHeaderHandler depends on AuthService).
builder.Services.AddHttpClient("ApiAuth", client => client.BaseAddress = new Uri(apiBase))
    .AddHttpMessageHandler<CookieHandler>();

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Api"));

// Localization — IStringLocalizer<AppStrings> resolves the RCL's .resx resources.
builder.Services.AddLocalization();
builder.Services.AddSingleton<ICulturePersistence, LocalStorageCulturePersistence>();
builder.Services.AddSingleton<IThemePersistence, LocalStorageThemePersistence>();
builder.Services.AddSingleton<IFileDownloadLauncher, BrowserFileDownloadLauncher>();
// Never notified on web — external flows return via a full redirect (fresh page load); the
// registration only satisfies the shared pages' injection (see AppResumeNotifier).
builder.Services.AddSingleton<AppResumeNotifier>();

// Web session store: the browser owns the HttpOnly refresh cookie, so this is a no-op.
builder.Services.AddSingleton<ISessionStore, CookieSessionStore>();

// Auth. AuthService is a SINGLETON: IHttpClientFactory resolves message handlers
// (AuthHeaderHandler) in a separate DI scope, so a scoped AuthService would give the
// handler a different instance with no token — and the Bearer header would never be
// attached. Singleton guarantees the app and the handler share one token store.
builder.Services.AddSingleton(sp => new AuthService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("ApiAuth"),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AuthService>>(),
    sp.GetRequiredService<ISessionStore>()));

var host = builder.Build();

// Apply the user's saved UI culture before the app renders, falling back to English.
// Signed-in users are further reconciled to their server-side locale by MainLayout.
var js = host.Services.GetRequiredService<IJSRuntime>();
var stored = await js.InvokeAsync<string?>("localStorage.getItem", LocalStorageCulturePersistence.StorageKey);
try
{
    var culture = string.IsNullOrWhiteSpace(stored) ? "en" : stored;
    CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(culture);
}
catch (CultureNotFoundException) { /* corrupt stored value — keep the default culture */ }

await host.RunAsync();

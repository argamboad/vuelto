using System.Globalization;
using Microsoft.Extensions.Logging;
using Perezosoft.Maui.Auth;
using Perezosoft.Shared.Ui;
using Perezosoft.Shared.Ui.Auth;

namespace Perezosoft.Maui;

public static class MauiProgram
{
	// Base address for the API, per platform.
	//  - Windows desktop reaches localhost directly over HTTPS (machine-trusted dev cert).
	//  - Android uses http://localhost:5238 via `adb reverse tcp:5238 tcp:5238`, which maps
	//    the device's localhost to the host. Using "localhost" (not 10.0.2.2) is what makes
	//    OAuth work: Google/Microsoft accept localhost as a redirect host but reject raw IPs,
	//    so the provider redirect_uri http://localhost:5238/signin-google is valid (and is
	//    the same one already registered for the desktop/web flow). See docs/MOBILE_TESTING.md.
	//  - PEREZOSOFT_API_BASE_URL overrides both (dev builds): the CI native smoke (NATIVE-7)
	//    points the app at its plain-HTTP stack, and a physical device can target a LAN API
	//    without recompiling.
	//
	// The localhost fallback + env override are DEBUG-ONLY (v3 NAT-3): a Release build must not ship
	// the dev localhost base (it would send OTP codes + refresh tokens cleartext to whatever binds
	// device-localhost). Release compiles in the real base from the $(ApiBaseUrl) build property —
	// the csproj fails the build if it's unset — surfaced here via AssemblyMetadata.
	private static string ApiBaseUrl =>
#if DEBUG
		Environment.GetEnvironmentVariable("PEREZOSOFT_API_BASE_URL") is { Length: > 0 } o ? o :
#if ANDROID
		"http://localhost:5238";
#else
		"https://localhost:7160";
#endif
#else
		ReleaseApiBaseUrl;

	// Release only: the base URL compiled in from $(ApiBaseUrl). Never throws in a real Release build —
	// the csproj's RequireApiBaseUrlInRelease target fails the build before we get here if it's unset.
	// (No LINQ: the using would be unused in Debug and trip warnings-as-errors.)
	private static string ReleaseApiBaseUrl
	{
		get
		{
			foreach (var attr in typeof(MauiProgram).Assembly
				.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
			{
				if (attr is System.Reflection.AssemblyMetadataAttribute { Key: "ApiBaseUrl", Value: { Length: > 0 } value })
					return value;
			}
			throw new InvalidOperationException(
				"Release build has no ApiBaseUrl compiled in — build with -p:ApiBaseUrl=https://your-api-host (v3 NAT-3).");
		}
	}
#endif

	// Custom URL scheme the Android app registers for the OAuth callback (perezosoft://auth).
	// Must match the API's Auth:Native:CallbackScheme and the manifest intent filter.
	private const string CallbackScheme = "perezosoft";

	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		// Localization — IStringLocalizer<AppStrings> resolves the RCL's .resx resources.
		// Apply the device-local saved culture before the first render (NATIVE-5, parity with
		// Web/Program.cs's localStorage bootstrap): the LanguageSwitcher persists the choice to
		// OS Preferences on native, the only store readable this early — the WebView (and its
		// localStorage) doesn't exist yet. No saved value → OS culture, the documented default
		// (docs/LOCALIZATION.md). Signed-in users are further reconciled to their server-side
		// locale by MainLayout.
		var savedCulture = Preferences.Default.Get<string?>(PreferencesCulturePersistence.PreferenceKey, null);
		if (!string.IsNullOrWhiteSpace(savedCulture))
		{
			try
			{
				CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(savedCulture);
			}
			catch (CultureNotFoundException) { /* corrupt stored value — keep the OS culture */ }
		}
		builder.Services.AddLocalization();
		builder.Services.AddSingleton<ICulturePersistence, PreferencesCulturePersistence>();

		// Theme needs no Preferences bootstrap: it's pure DOM, and theme.js reads the
		// WebView's own localStorage before first paint (THEME-1).
		builder.Services.AddSingleton<IThemePersistence, LocalStorageThemePersistence>();

		// Signed-URL downloads can't ride a WebView navigation — fetch + OS share sheet instead
		// (NATIVE-3). Uses the default (Bearer) client registered below: the signed URL itself
		// needs no auth, but absolute URLs bypass BaseAddress so the same client serves both.
		builder.Services.AddSingleton<IFileDownloadLauncher>(sp =>
			new ShareFileDownloadLauncher(sp.GetRequiredService<HttpClient>()));

		// Fired by the window's Resumed lifecycle event (App.CreateWindow) so pages can refresh
		// after an external round-trip returns to the app (NATIVE-4, G2).
		builder.Services.AddSingleton<AppResumeNotifier>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		// Refresh token lives in the OS secure store (the native equivalent of the web's
		// HttpOnly cookie). OAuth is platform-specific: Windows captures the callback via a
		// loopback HTTP listener; Android/iOS/macCatalyst via a custom-scheme WebAuthenticator.
		// Both sit behind IOAuthInitiator so AuthService and the Login page stay
		// platform-agnostic. EVERY target platform must register one — the AuthService factory
		// below resolves it with GetRequiredService, so a missing branch here crashes the app at
		// first resolve (exactly how iOS/macCatalyst were dead-on-arrival before G7).
#if MACCATALYST && DEBUG
		// Ad-hoc-signed local builds can't reach the data-protection keychain (restricted
		// entitlement) — see DebugFileSessionStore. Signed builds use the secure store below.
		builder.Services.AddSingleton<ISessionStore, DebugFileSessionStore>();
#else
		builder.Services.AddSingleton<ISessionStore, SecureStorageSessionStore>();
#endif
#if ANDROID || IOS || MACCATALYST
		builder.Services.AddSingleton<IOAuthInitiator>(sp =>
			new WebAuthenticatorOAuthInitiator(ApiBaseUrl, CallbackScheme, sp.GetRequiredService<ILogger<WebAuthenticatorOAuthInitiator>>()));
		// WebAuthenticator's pending state is in-memory only — persist an in-flight marker
		// (+ a stashed callback on Android cold starts) so OAuth survives the OS killing
		// the process during the browser round-trip (NATIVE-12). Windows' loopback flow
		// blocks in-process and has no custom-scheme relaunch, so it registers no store.
		builder.Services.AddSingleton<IOAuthResumeStore, PreferencesOAuthResumeStore>();
#elif WINDOWS
		builder.Services.AddSingleton<IOAuthInitiator>(sp =>
			new LoopbackOAuthInitiator(ApiBaseUrl, sp.GetRequiredService<ILogger<LoopbackOAuthInitiator>>()));
#endif

		// Two clients, mirroring the Web host (Api / ApiAuth) to avoid a DI cycle:
		//
		//  - AuthService gets its OWN client with NO Bearer handler. refresh/logout/otp/
		//    exchange are anonymous or carry the body refresh token — they need no Bearer,
		//    and this is what the handler below would depend on, so keep them separate.
		//  - The default HttpClient (what the RCL pages inject) attaches the in-memory JWT
		//    as a Bearer header so [Authorize] endpoints (household, linked logins) work.
		//
		// The X-Native-Client header on both selects the API's body-token transport.
		builder.Services.AddSingleton(sp =>
		{
			var authClient = new HttpClient { BaseAddress = new Uri(ApiBaseUrl) };
			authClient.DefaultRequestHeaders.Add("X-Native-Client", "true");
			return new AuthService(
				authClient,
				sp.GetRequiredService<ILogger<AuthService>>(),
				sp.GetRequiredService<ISessionStore>(),
				sp.GetRequiredService<IOAuthInitiator>(),
				sp.GetService<IOAuthResumeStore>()); // null on Windows (loopback flow)
		});

		builder.Services.AddSingleton(sp =>
		{
			var handler = new NativeAuthHeaderHandler(sp.GetRequiredService<AuthService>())
			{
				InnerHandler = new HttpClientHandler()
			};
			var client = new HttpClient(handler) { BaseAddress = new Uri(ApiBaseUrl) };
			client.DefaultRequestHeaders.Add("X-Native-Client", "true");
			return client;
		});

		return builder.Build();
	}
}

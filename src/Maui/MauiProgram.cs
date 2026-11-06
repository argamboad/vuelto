using Perezosoft.Client.Maui;
using Vuelto.Shared.Ui;
using Perezosoft.Client;

namespace Vuelto.Maui;

/// <summary>
/// The MAUI host (Pkg 4.3, ADR-034): the platform's shell rooted on this app's UI, the platform's client for a device
/// (Perezosoft.Platform.Maui's UsePlatformMaui), and what only this app can say — its API base, its callback scheme,
/// its build, its system-bar colours, its fonts.
/// </summary>
public static class MauiProgram
{
	// Base address for the API, per platform.
	//  - Windows desktop reaches localhost directly over HTTPS (machine-trusted dev cert).
	//  - Android uses the API's local http port via `adb reverse` (the csproj bridges it), which maps
	//    the device's localhost to the host. Using "localhost" (not 10.0.2.2) is what makes
	//    OAuth work: Google/Microsoft accept localhost as a redirect host but reject raw IPs,
	//    so the provider redirect_uri http://localhost:<api http port>/signin-google is valid (and is
	//    the same one already registered for the desktop/web flow). See docs/MOBILE_TESTING.md.
	//  - VUELTO_API_BASE_URL overrides both (dev builds): the CI native smoke (NATIVE-7)
	//    points the app at its plain-HTTP stack, and a physical device can target a LAN API
	//    without recompiling.
	//
	// The localhost fallback + env override are DEBUG-ONLY (v3 NAT-3): a Release build must not ship
	// the dev localhost base (it would send OTP codes + refresh tokens cleartext to whatever binds
	// device-localhost). Release compiles in the real base from the $(ApiBaseUrl) build property —
	// the release guards fail the build if it's unset — surfaced here via AssemblyMetadata. Decided
	// here, in the app, because it depends on THIS build's configuration; the platform trims the slash.
	private static string ApiBaseUrl =>
#if DEBUG
		Environment.GetEnvironmentVariable("VUELTO_API_BASE_URL") is { Length: > 0 } o ? o :
#if ANDROID
		LocalPorts.ApiHttpUrl;   // local-ports.props
#else
		LocalPorts.ApiHttpsUrl;  // local-ports.props
#endif
#else
		ReleaseApiBaseUrl;

	// Release only: the base URL compiled in from $(ApiBaseUrl). Never throws in a real Release build —
	// the release guards' ValidateReleaseApiBaseUrl fails the build before we get here if it's unset.
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

	// Custom URL scheme the Android app registers for the OAuth callback (vuelto://auth).
	// Must match the API's Auth:Native:CallbackScheme and the callback activity's intent filter.
	private const string CallbackScheme = "vuelto";

#if DEBUG
	private const bool DebugBuild = true;
#else
	private const bool DebugBuild = false;
#endif

	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UsePlatformShell<App>("¿Y el vuelto?")
			.UsePlatformMaui(new PlatformMauiOptions
			{
				ApiBaseUrl = ApiBaseUrl,
				CallbackScheme = CallbackScheme,
				DebugBuild = DebugBuild,
#if ANDROID
				SystemBars = SystemBarColors.Palette,
#endif
			})
			.ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));
		// vuelto's own client services (Shared.Ui): the review-queue badge and the ₡ · $ · both preference.
		builder.Services.AddSingleton<ReviewQueueNotifier>(); // EMAIL-6: the header badge re-counts when the queue changes
		builder.Services.AddScoped<DisplayCurrencyStore>();   // DISPLAY-1: device copy + account copy
		return builder.Build();
	}
}

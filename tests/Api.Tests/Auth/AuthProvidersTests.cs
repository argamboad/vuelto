using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Services;

namespace Vuelto.Api.Tests.Auth;

/// <summary>
/// The OAuth provider-discovery logic behind <c>GET /api/auth/providers</c> (used by the login/settings
/// UIs to render only configured providers). A provider counts as "enabled" iff its OAuth scheme is
/// actually registered — which the app only does when the provider's ClientId is configured.
/// </summary>
public class AuthProvidersTests
{
    [Fact]
    public async Task EnabledAsync_WhenNoOAuthSchemesRegistered_IsEmpty()
    {
        var schemes = BuildSchemeProvider(); // nothing registered

        Assert.Empty(await AuthProviders.EnabledAsync(schemes));
    }

    [Fact]
    public async Task EnabledAsync_ReturnsOnlyTheRegisteredProviders()
    {
        // Register just Google's scheme (as a configured deployment would).
        var schemes = BuildSchemeProvider(AuthProviders.SchemeFor(AuthProviders.Google)!);

        var enabled = await AuthProviders.EnabledAsync(schemes);

        Assert.Equal([AuthProviders.Google], enabled);         // google present…
        Assert.DoesNotContain(AuthProviders.Microsoft, enabled); // …microsoft absent
    }

    private static IAuthenticationSchemeProvider BuildSchemeProvider(params string[] schemeNames)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var auth = services.AddAuthentication();
        foreach (var name in schemeNames)
            auth.AddScheme<AuthenticationSchemeOptions, NoopAuthHandler>(name, _ => { });
        return services.BuildServiceProvider().GetRequiredService<IAuthenticationSchemeProvider>();
    }

    // A do-nothing handler so a scheme can be registered by name without a real OAuth provider.
    private sealed class NoopAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}

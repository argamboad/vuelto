using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vuelto.Shared.Ui.Auth;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// Native sign-in on the login page. The Windows loopback flow waits up to five minutes for the browser to
/// come back; abandoning the browser used to leave both provider buttons dead until that timeout ("close and
/// reopen the app", owner, 2026-09-14). The page now offers Cancel while the browser flow is in flight.
/// </summary>
public class LoginNativeOAuthTests : ComponentTestBase
{
    /// <summary>A browser flow that never returns on its own — only cancellation ends it, as an abandoned tab would not.</summary>
    private sealed class HangingInitiator : IOAuthInitiator
    {
        public bool Started { get; private set; }
        public CancellationToken Token { get; private set; }

        public async Task<IReadOnlyDictionary<string, string>?> RunBrowserFlowAsync(string provider, string? linkToken = null, CancellationToken cancellationToken = default)
        {
            Started = true;
            Token = cancellationToken;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { /* the loopback listener answers null once cancelled */ }
            return null;
        }
    }

    private HangingInitiator NativeSignIn()
    {
        var initiator = new HangingInitiator();
        var native = new AuthService(
            new HttpClient(Http) { BaseAddress = new Uri("http://localhost") },
            NullLogger<AuthService>.Instance,
            new FakeSessionStore(usesBodyTransport: true),
            initiator);
        Services.AddSingleton(native); // the last registration wins: the page sees a native AuthService
        Http.On(HttpMethod.Get, "/api/auth/providers", """{"providers":["google"]}""");
        return initiator;
    }

    [Fact]
    public void AnAbandonedBrowserFlow_CanBeCancelled_FromTheLoginPage()
    {
        var initiator = NativeSignIn();

        var cut = Render<Login>();
        cut.WaitForElement("button.btn-google");
        cut.Find("button.btn-google").Click();

        // In flight: the provider buttons are held, the page says it is waiting, and offers a way out.
        cut.WaitForAssertion(() => Assert.True(initiator.Started));
        Assert.True(cut.Find("button.btn-google").HasAttribute("disabled"));
        Assert.Contains("Login_OAuthWaiting", cut.Find("[data-testid='login-oauth-waiting']").TextContent);

        cut.Find("[data-testid='login-oauth-cancel']").Click();

        // Cancelled: the initiator's token fired, the buttons are back, and a deliberate cancel is not an error.
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.btn-google").HasAttribute("disabled")));
        Assert.True(initiator.Token.IsCancellationRequested);
        Assert.Empty(cut.FindAll("[data-testid='login-oauth-waiting']"));
        Assert.Empty(cut.FindAll("[data-testid='login-oauth-cancel']"));
        Assert.Empty(cut.FindAll(".alert-danger"));
    }

    [Fact]
    public void BeforeAnyClick_NothingSaysWaiting()
    {
        NativeSignIn();
        var cut = Render<Login>();
        cut.WaitForElement("button.btn-google");
        Assert.Empty(cut.FindAll("[data-testid='login-oauth-waiting']"));
        Assert.Empty(cut.FindAll("[data-testid='login-oauth-cancel']"));
    }
}

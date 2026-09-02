using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Perezosoft.Shared.Ui.Auth;

namespace Perezosoft.Core.Tests.Auth;

/// <summary>
/// The native OTP verify path must surface the server's lockout signal (too_many_attempts) rather
/// than collapsing every failure to a blank "incorrect or expired" — parity with the web path, so a
/// locked-out native user is told to wait, not to retry (QA-AUTH-04, CONF-5). The wrong/expired case
/// stays generic (CONF-6).
/// </summary>
public class NativeOtpLockoutTests
{
    [Fact]
    public async Task VerifyOtp_Lockout_SurfacesTooManyAttemptsCode()
    {
        var auth = NewAuthService(HttpStatusCode.Unauthorized, """{"error":"too_many_attempts","message":"..."}""");

        var result = await auth.VerifyOtpAsync("brute@example.com", "000000");

        Assert.Equal(SignInStatus.Failed, result.Status);
        Assert.Equal("too_many_attempts", result.Error);
    }

    [Fact]
    public async Task VerifyOtp_WrongCode_SurfacesGenericCode()
    {
        var auth = NewAuthService(HttpStatusCode.Unauthorized, """{"error":"invalid_code","message":"..."}""");

        var result = await auth.VerifyOtpAsync("someone@example.com", "000000");

        Assert.Equal(SignInStatus.Failed, result.Status);
        Assert.Equal("invalid_code", result.Error);
    }

    [Fact]
    public async Task VerifyOtp_FailureWithNoBody_LeavesErrorNull()
    {
        var auth = NewAuthService(HttpStatusCode.Unauthorized, body: "");

        var result = await auth.VerifyOtpAsync("someone@example.com", "000000");

        Assert.Equal(SignInStatus.Failed, result.Status);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("too_many_attempts", "Login_ErrTooManyAttempts")]
    [InlineData("invalid_code", "Login_ErrCodeIncorrect")]
    [InlineData(null, "Login_ErrCodeIncorrect")]
    [InlineData("something_else", "Login_ErrCodeIncorrect")]
    public void OtpErrorKey_MapsLockoutDistinctly_EverythingElseGeneric(string? code, string expectedKey) =>
        Assert.Equal(expectedKey, AuthErrorCopy.OtpErrorKey(code));

    private static AuthService NewAuthService(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("https://api.test") };
        return new AuthService(http, NullLogger<AuthService>.Instance, new FakeSessionStore());
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    // Native transport shape; the failure path never touches the store, so the members are inert.
    private sealed class FakeSessionStore : ISessionStore
    {
        public bool UsesBodyTransport => true;
        public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>(null);
        public Task SaveRefreshTokenAsync(string refreshToken) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
}

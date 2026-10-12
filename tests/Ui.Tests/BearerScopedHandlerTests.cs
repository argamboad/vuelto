using System.Net;
using Xunit;
using Vuelto.Shared.Ui.Auth;
using Vuelto.Ui.Tests.Infrastructure;

namespace Vuelto.Ui.Tests;

/// <summary>
/// v4 T49 (NAT-12, R102): the one bearer handler both hosts install attaches the token — and renews on a 401 —
/// only for the API's own requests. A foreign absolute URL (a presigned S3/MinIO download, a CDN) goes out
/// untouched: no Authorization header (AWS rejects a presigned request that carries one) and no refresh spent
/// on the file host's behalf.
/// </summary>
public class BearerScopedHandlerTests : ComponentTestBase
{
    private static readonly Uri ApiOrigin = new("http://localhost");

    private HttpClient Client() =>
        new(new BearerScopedHandler(Auth, ApiOrigin) { InnerHandler = Http }) { BaseAddress = ApiOrigin };

    private int Refreshes => Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/auth/refresh");

    [Fact]
    public async Task ApiRequest_CarriesTheBearer()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/household", "{}");

        await Client().GetAsync("/api/household");

        var sent = Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/household");
        Assert.Equal(Auth.AccessToken, sent.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task AbsoluteUrl_OnTheApiOrigin_CarriesTheBearer_Too()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/files/tok", "{}");

        await Client().GetAsync("http://localhost/api/files/tok"); // the local-disk download URL with a public origin set

        var sent = Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/files/tok");
        Assert.NotNull(sent.Headers.Authorization);
    }

    [Fact]
    public async Task ForeignAbsoluteUrl_GetsNoHeader_AndSpendsNoRefresh()
    {
        await SignInAsync();
        var before = Refreshes;
        Http.On(HttpMethod.Get, "/bucket/tenant/export.json", "{}");

        await Client().GetAsync("https://files.example.net/bucket/tenant/export.json?X-Amz-Signature=abc");

        var sent = Assert.Single(Http.Requests, r => r.RequestUri!.Host == "files.example.net");
        Assert.Null(sent.Headers.Authorization);
        Assert.Equal(before, Refreshes);
    }

    [Fact]
    public async Task ForeignHost401_IsNotRetried_AndSpendsNoRefresh()
    {
        await SignInAsync();
        var before = Refreshes;
        Http.On(HttpMethod.Get, "/private", "{}", HttpStatusCode.Unauthorized);

        var response = await Client().GetAsync("https://vendor.example.net/private");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(Http.Requests, r => r.RequestUri!.Host == "vendor.example.net"); // one send, no resend
        Assert.Equal(before, Refreshes);
    }

    [Theory]
    [InlineData("/api/x", true)]
    [InlineData("http://localhost/api/x", true)]
    [InlineData("HTTP://LOCALHOST:80/api/x", true)]     // default port, case-insensitive scheme/host
    [InlineData("https://localhost/api/x", false)]      // another scheme is another origin
    [InlineData("http://localhost:5001/api/x", false)]  // another port is another origin
    [InlineData("http://evil.localhost/api/x", false)]  // a subdomain is not the origin
    public void IsApiRequest_MatchesTheOriginExactly(string url, bool expected) =>
        Assert.Equal(expected, BearerRetry.IsApiRequest(new Uri(url, UriKind.RelativeOrAbsolute), ApiOrigin));
}

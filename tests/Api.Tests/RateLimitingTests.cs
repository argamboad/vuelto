using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vuelto.Api.Configuration;
using Vuelto.Api.Services;

namespace Vuelto.Api.Tests;

/// <summary>
/// Exercises the real rate-limiter policies through actual middleware in a minimal host (no DB / SMTP):
/// the passwordless endpoints throttle per IP (CONF-5), and the public API throttles <b>per key</b>
/// (PUBAPI-2) so one tenant's key can't exhaust another's budget. Hosting the shared
/// <c>AddApiRateLimiters</c> extension proves the production policy, not a copy.
/// </summary>
public class RateLimitingTests
{
    private static async Task<TestServer> StartHostAsync()
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer()
               .ConfigureServices(services =>
               {
                   services.AddRouting();
                   services.AddApiRateLimiters();
               })
               .Configure(app =>
               {
                   // UseRateLimiter must run AFTER routing so it can read the endpoint's
                   // RequireRateLimiting metadata (same order as the real Program.cs pipeline).
                   app.UseRouting();
                   // Simulate API-key auth: a test partitions on the X-Test-Key header (the real public
                   // policy partitions on the NameIdentifier claim the ApiKey scheme sets).
                   // ...and the webhook-write policy partitions on the tenant_id claim (X-Test-Tenant here).
                   app.Use(async (ctx, next) =>
                   {
                       var claims = new List<Claim>();
                       if (ctx.Request.Headers["X-Test-Key"].ToString() is { Length: > 0 } key)
                           claims.Add(new Claim(ClaimTypes.NameIdentifier, key));
                       if (ctx.Request.Headers["X-Test-Tenant"].ToString() is { Length: > 0 } tenant)
                           claims.Add(new Claim(JwtClaims.TenantId, tenant));
                       if (claims.Count > 0)
                           ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                       await next();
                   });
                   app.UseRateLimiter();
                   app.UseEndpoints(endpoints =>
                   {
                       endpoints.MapPost("/otp/send", () => Results.Ok())
                                .RequireRateLimiting(RateLimiting.PasswordlessPolicy);
                       endpoints.MapPost("/otp/verify", () => Results.Ok())
                                .RequireRateLimiting(RateLimiting.PasswordlessVerifyPolicy);
                       endpoints.MapGet("/pub", () => Results.Ok())
                                .RequireRateLimiting(RateLimiting.PublicApiPolicy);
                       endpoints.MapPost("/refresh", () => Results.Ok())
                                .RequireRateLimiting(RateLimiting.RefreshPolicy);
                       endpoints.MapPost("/hook/test", () => Results.Ok())
                                .RequireRateLimiting(RateLimiting.WebhookWritePolicy);
                       endpoints.MapPost("/hook/replay", () => Results.Ok())
                                .RequireRateLimiting(RateLimiting.WebhookWritePolicy);
                   });
               });
        });
        var host = await builder.StartAsync();
        return host.GetTestServer();
    }

    [Fact]
    public async Task PasswordlessEndpoint_FloodedFromOneClient_Returns429AfterTheLimit()
    {
        using var server = await StartHostAsync();
        var client = server.CreateClient();

        // The first PermitLimit requests pass...
        for (var i = 0; i < RateLimiting.PermitLimit; i++)
        {
            var ok = await client.PostAsync("/otp/send", content: null);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // ...the next one trips the limiter.
        var tripped = await client.PostAsync("/otp/send", content: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, tripped.StatusCode);
    }

    [Fact]
    public async Task OtpVerify_HasIndependentBudget_SizedAboveTheAttemptCap_SoTheLockoutIsReachable()
    {
        using var server = await StartHostAsync();
        var client = server.CreateClient();

        // Exhaust the whole SEND budget from this IP (the email-bomb guard).
        for (var i = 0; i < RateLimiting.PermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/otp/send", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/otp/send", content: null)).StatusCode);

        // VERIFY must still have its own, untouched budget — sized above the OTP attempt cap so the
        // server-side 401 too_many_attempts lockout returns BEFORE this throttle's 429 can mask it
        // (the reported bug: after 5 attempts the UI showed the generic "Verification failed").
        var verifyBudget = RateLimiting.VerifyPermitFor(RateLimiting.PermitLimit, otpMaxAttempts: 5);
        Assert.True(verifyBudget > 5, "verify budget must exceed the OTP attempt cap so the lockout wins the race");
        for (var i = 0; i < verifyBudget; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/otp/verify", content: null)).StatusCode);

        // ...and only past its own, larger budget does the verify throttle finally trip.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/otp/verify", content: null)).StatusCode);
    }

    [Fact]
    public async Task Refresh_FloodedFromOneClient_Returns429AfterTheLimit()
    {
        // v4 AUTH-1 #4 (T36): /api/auth/refresh had no limiter, so someone holding a stolen token could mint
        // rows cheaply. Per IP; the budget is far above what the keep-alive needs (one call an hour per client).
        using var server = await StartHostAsync();
        var client = server.CreateClient();

        for (var i = 0; i < RateLimiting.RefreshPermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/refresh", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/refresh", content: null)).StatusCode);
    }

    [Fact]
    public void RefreshEndpoint_CarriesTheRefreshPolicy()
    {
        // The policy above is only worth anything if the real endpoint asks for it.
        var refresh = typeof(Vuelto.Api.Controllers.AuthController).GetMethod("Refresh")!;
        var attribute = refresh.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>().SingleOrDefault();

        Assert.NotNull(attribute);
        Assert.Equal(RateLimiting.RefreshPolicy, attribute!.PolicyName);
    }

    [Fact]
    public async Task PublicApi_ThrottlesPerKey_AndKeysAreIsolated()
    {
        using var server = await StartHostAsync();
        var client = server.CreateClient();

        // Key A spends its whole budget...
        for (var i = 0; i < RateLimiting.PublicApiPermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK, (await Get(client, "/pub", "key-A")).StatusCode);

        // ...the next request from A is throttled...
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Get(client, "/pub", "key-A")).StatusCode);

        // ...but key B has its own untouched budget.
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/pub", "key-B")).StatusCode);
    }

    [Fact]
    public async Task WebhookWrites_AreRateLimitedPerTenant_AndTenantsAreIsolated()
    {
        // v4 T44 (JOBS-7): the test-send and the replay share ONE per-tenant budget — each call is a delivery row
        // and an outbound signed POST, so the owner's pace is bounded per tenant, not per IP (a household's devices
        // share an IP; two tenants must not share a budget).
        using var server = await StartHostAsync();
        var client = server.CreateClient();

        for (var i = 0; i < RateLimiting.WebhookWritePermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAs(client, i % 2 == 0 ? "/hook/test" : "/hook/replay", "tenant-A")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAs(client, "/hook/test", "tenant-A")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAs(client, "/hook/replay", "tenant-A")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAs(client, "/hook/test", "tenant-B")).StatusCode);
    }

    private static Task<HttpResponseMessage> PostAs(HttpClient client, string path, string tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Test-Tenant", tenant);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-Key", key);
        return client.SendAsync(request);
    }
}

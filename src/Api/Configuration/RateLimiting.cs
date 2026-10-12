using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Services;

namespace Vuelto.Api.Configuration;

/// <summary>
/// Rate-limiting policies for abuse-prone endpoints. The passwordless endpoints are an
/// unauthenticated email-bomb / outbound-cost amplifier and a brute-force surface, so they're
/// throttled per client IP (CONF-5). The per-email dimension — making resends unable to reset a
/// brute-force budget across IPs — is enforced independently and IP-agnostically by the cumulative
/// OTP lockout in <c>PasswordlessService.RedeemOtpAsync</c>.
///
/// <para><b>Send and verify get SEPARATE budgets on purpose.</b> They used to share one per-IP
/// window, so the <c>/otp/send</c> that issues the code plus a few guesses would exhaust the budget
/// and the limiter's 429 masked the OTP lockout's distinct 401 <c>too_many_attempts</c> — the user
/// saw a generic "verification failed" instead of the lockout message. Splitting them (and sizing the
/// verify budget above the attempt cap via <see cref="VerifyPermitFor"/>) lets the server-side lockout
/// win the race.</para>
/// </summary>
public static class RateLimiting
{
    /// <summary>Named policy for the email-producing send endpoints: <c>/otp/send</c>, <c>/magic-link/send</c>.</summary>
    public const string PasswordlessPolicy = "passwordless";

    /// <summary>Named policy for the guess-checking verify endpoints: <c>/otp/verify</c>, <c>/mfa/verify</c>.
    /// Separate budget from <see cref="PasswordlessPolicy"/> so the server-side lockout's 401 fires first.</summary>
    public const string PasswordlessVerifyPolicy = "passwordless-verify";

    /// <summary>Per-API-key throttle for the public API (PUBAPI-2) — partitions by the key id.</summary>
    public const string PublicApiPolicy = "public-api";

    /// <summary>
    /// Per-IP throttle for <c>/api/auth/refresh</c> (v4 T36, decision #4): the keep-alive needs one call an hour
    /// per client, so even a household's worth of devices behind one NAT stays far under the budget, while someone
    /// holding a stolen token can't mint rows cheaply. Overridable via <c>Auth:RateLimit:RefreshPermitLimit</c>
    /// (raised for the E2E stack, whose whole browser suite shares one source IP).
    /// </summary>
    public const string RefreshPolicy = "refresh";
    public const int RefreshPermitLimit = 60;

    /// <summary>
    /// Per-TENANT throttle for the two webhook writes that cost something per call (v4 T44, JOBS-7): the
    /// synchronous test-send (a delivery row + a signed outbound POST) and the replay (an outbox row + a
    /// delivery on dispatch). Both are owner-only, so the budget is the tenant's, shared by the pair: an owner
    /// can neither grow the delivery table nor fire at a third party's URL faster than this.
    /// </summary>
    public const string WebhookWritePolicy = "webhook-write";
    public const int WebhookWritePermitLimit = 10;

    /// <summary>Default requests allowed per IP per <see cref="Window"/> before the limiter returns 429.
    /// Overridable via <c>Auth:RateLimit:PasswordlessPermitLimit</c> (e.g. raised for the E2E stack,
    /// where the whole browser suite shares one source IP); production keeps this default.</summary>
    public const int PermitLimit = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Headroom the verify budget keeps above the OTP attempt cap (<c>Auth:Otp:MaxAttempts</c>)
    /// so the 401 <c>too_many_attempts</c> lockout always returns before this throttle's 429 can mask it.</summary>
    public const int VerifyPermitBuffer = 5;

    /// <summary>The per-IP verify budget: the larger of the send limit and (attempt cap + buffer), so a
    /// user can always reach the attempt cap and see the distinct lockout message. Shared by production
    /// wiring and the rate-limit tests so the two can't drift.</summary>
    public static int VerifyPermitFor(int passwordlessLimit, int otpMaxAttempts) =>
        Math.Max(passwordlessLimit, otpMaxAttempts + VerifyPermitBuffer);

    /// <summary>Requests allowed per API key per <see cref="PublicApiWindow"/> before 429.</summary>
    public const int PublicApiPermitLimit = 60;
    public static readonly TimeSpan PublicApiWindow = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddApiRateLimiters(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var passwordlessLimit = configuration?.GetValue("Auth:RateLimit:PasswordlessPermitLimit", PermitLimit) ?? PermitLimit;
        var otpMaxAttempts = configuration?.GetValue("Auth:Otp:MaxAttempts", 5) ?? 5;
        var verifyLimit = VerifyPermitFor(passwordlessLimit, otpMaxAttempts);
        var refreshLimit = configuration?.GetValue("Auth:RateLimit:RefreshPermitLimit", RefreshPermitLimit) ?? RefreshPermitLimit;
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Refresh (v4 T36): per IP. A held refresh token is a credential; minting sessions with it must cost
            // something per source, without touching the keep-alive's one call an hour per client.
            options.AddPolicy(RefreshPolicy, httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = refreshLimit,
                    Window = Window,
                    QueueLimit = 0,
                });
            });

            // Webhook test-send + replay (v4 T44): per tenant. The routes are owner-authenticated, so the principal
            // carries the tenant id by the time the limiter runs; the IP is only the fallback for a missing claim.
            options.AddPolicy(WebhookWritePolicy, httpContext =>
            {
                var tenant = httpContext.User.FindFirst(JwtClaims.TenantId)?.Value
                             ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
                return RateLimitPartition.GetFixedWindowLimiter(tenant, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = WebhookWritePermitLimit,
                    Window = Window,
                    QueueLimit = 0,
                });
            });

            // Passwordless SEND endpoints (email-producing): per-IP email-bomb / outbound-cost guard (CONF-5).
            options.AddPolicy(PasswordlessPolicy, httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = passwordlessLimit,
                    Window = Window,
                    QueueLimit = 0,
                });
            });

            // Passwordless VERIFY endpoints (guess-checking): separate per-IP budget sized above the OTP
            // attempt cap so the server-side lockout's 401 too_many_attempts returns before this 429 can
            // mask it. Brute force across resends/IPs is still bounded by the cumulative OTP lockout.
            options.AddPolicy(PasswordlessVerifyPolicy, httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = verifyLimit,
                    Window = Window,
                    QueueLimit = 0,
                });
            });

            // Public API: per-key (PUBAPI-2). The endpoint is API-key-authenticated, so by the time the
            // limiter runs the principal carries the key id (NameIdentifier) — partition on it so one
            // tenant's key can't exhaust another's budget. Falls back to IP if somehow unauthenticated.
            options.AddPolicy(PublicApiPolicy, httpContext =>
            {
                var keyId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                            ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
                return RateLimitPartition.GetFixedWindowLimiter(keyId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PublicApiPermitLimit,
                    Window = PublicApiWindow,
                    QueueLimit = 0,
                });
            });
        });
    }
}

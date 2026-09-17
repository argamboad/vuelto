using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Vuelto.Api.Authentication;

namespace Vuelto.Api.Features.Reports.Pdf;

/// <summary>
/// REPORTS-8 (plan A9): "Email me this report" may run at most <see cref="PermitLimit"/> times per person per
/// <see cref="Window"/>, so a stuck button or a loop can't drain the free email quota (Brevo: 300 a day for the whole
/// app). Partitioned by the signed-in user; a fixed window held in memory, so a restart resets the count — enough for
/// its purpose. Registered by the app on top of the platform's limiter (<c>AddApiRateLimiters</c> stays untouched);
/// the platform's options already answer 429.
/// </summary>
public static class ReportEmailRateLimit
{
    public const string Policy = "report-email";
    public const int PermitLimit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    public static IServiceCollection AddReportEmailRateLimit(this IServiceCollection services) =>
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(Policy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.User.GetUserId()?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window, QueueLimit = 0 })));
}

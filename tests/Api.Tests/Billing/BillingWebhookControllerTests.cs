using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vuelto.Api.Controllers;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Billing;
using Vuelto.Infrastructure.Inbox;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// GAP-5 (v2 audit): a forged/invalid billing-webhook signature must be observable — the endpoint is
/// anonymous, so a rejected probe should leave a warning (with source IP) rather than a silent 400.
/// A valid delivery logs no warning. Real handler over Postgres; the invalid-signature path returns
/// before any DB work (the fake provider throws on parse), so no tenant write occurs.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BillingWebhookControllerTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task ForgedSignature_Returns400_AndLogsWarning_WithSourceIp()
    {
        await using var db = Fixture.CreateContext();
        var logger = new CapturingLogger<BillingWebhookController>();
        var controller = NewController(db, logger, remoteIp: "203.0.113.9",
            body: JsonSerializer.Serialize(Event(Guid.CreateVersion7())), signature: "forged");

        var result = await controller.Receive(default);

        Assert.IsType<BadRequestObjectResult>(result);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("203.0.113.9", warning.Message);
        Assert.Empty(await db.Set<Subscription>().IgnoreQueryFilters().ToListAsync()); // nothing applied
    }

    [Fact]
    public async Task ValidSignature_Applies_NoWarning()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        var logger = new CapturingLogger<BillingWebhookController>();
        var controller = NewController(db, logger, remoteIp: "203.0.113.9",
            body: JsonSerializer.Serialize(Event(tenant)), signature: FakeBillingProvider.ValidSignature);

        var result = await controller.Receive(default);

        Assert.IsType<OkResult>(result);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // --- helpers ---

    private static BillingWebhookEvent Event(Guid tenant) =>
        new("evt_default", tenant, PlanKeys.Pro, SubscriptionStatus.Active, "cus_1", "sub_1",
            DateTimeOffset.UtcNow.AddDays(30), OccurredAt: DateTimeOffset.UtcNow);

    private BillingWebhookController NewController(
        AppDbContext db, ILogger<BillingWebhookController> logger, string remoteIp, string body, string signature)
    {
        var current = new HttpCurrentTenant(new HttpContextAccessor()); // no JWT — the handler enters the event's tenant
        var handler = new BillingWebhookHandler(
            new FakeBillingProvider(),
            new EfInbox(db, TimeProvider.System),
            new EfRepository<Subscription>(db),
            current,
            new EfUnitOfWork(db),
            BuildNotifier(db),
            TimeProvider.System);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        httpContext.Request.Headers["Stripe-Signature"] = signature;

        return new BillingWebhookController(handler, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static IBillingNotifier BuildNotifier(AppDbContext db) =>
        new BillingNotifier(
            new TenantRepository(db),
            new NotificationService(
                new EfRepository<Notification>(db), new EfRepository<NotificationPreference>(db),
                new UserRepository(db), new NoopEmailSender(), TimeProvider.System));
}

/// <summary>Records level + rendered message of every log entry.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

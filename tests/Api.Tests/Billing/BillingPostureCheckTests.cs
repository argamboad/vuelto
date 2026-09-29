using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vuelto.Api.Configuration;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// v4 T46 (BILL-1): with the gate off the webhook route is gone, so a Stripe-managed subscription can neither be
/// cancelled through the app nor lapse by itself — and because resolution is gate-blind, its tenant keeps the
/// plan while Stripe keeps charging. The only place to notice is the startup log; this is the one line.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BillingPostureCheckTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task GateOff_WithALiveStripeManagedRow_WarnsOnce_WithTheCount()
    {
        var tenant = Guid.CreateVersion7();
        await SeedAsync(tenant, stripeSubscriptionId: "sub_lingering", status: SubscriptionStatus.Active);
        await SeedAsync(Guid.CreateVersion7(), stripeSubscriptionId: "sub_gone", status: SubscriptionStatus.Canceled); // not live: not counted
        await SeedAsync(Guid.CreateVersion7(), stripeSubscriptionId: null, status: SubscriptionStatus.Active);         // a comp: not Stripe's
        var log = new CapturingLogger<BillingPostureCheck>();

        await new BillingPostureCheck(new BillingSettings { Enabled = false }, Scopes(), log).StartAsync(default);

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("1 tenant(s)", warning.Message);
        Assert.Contains("Stripe keeps charging", warning.Message);
    }

    [Fact]
    public async Task GateOn_SaysNothing_EvenWithStripeRows()
    {
        await SeedAsync(Guid.CreateVersion7(), stripeSubscriptionId: "sub_live", status: SubscriptionStatus.Active);
        var log = new CapturingLogger<BillingPostureCheck>();

        await new BillingPostureCheck(new BillingSettings { Enabled = true }, Scopes(), log).StartAsync(default);

        Assert.Empty(log.Entries);
    }

    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Fixture.CreateContext());
        services.AddScoped<IRepository<Subscription>>(sp => new EfRepository<Subscription>(sp.GetRequiredService<Vuelto.Infrastructure.Persistence.AppDbContext>()));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private async Task SeedAsync(Guid tenant, string? stripeSubscriptionId, string status)
    {
        await using var db = Fixture.CreateContext(tenant);
        db.Set<Tenant>().Add(new Tenant { Id = tenant, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = PlanKeys.Pro, Status = status, StripeSubscriptionId = stripeSubscriptionId,
            StripeCustomerId = stripeSubscriptionId is null ? null : "cus_x",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}

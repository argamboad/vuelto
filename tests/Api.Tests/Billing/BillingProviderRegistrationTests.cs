using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Abstractions;
using Perezosoft.Infrastructure;
using Perezosoft.Infrastructure.Billing;

namespace Perezosoft.Api.Tests.Billing;

/// <summary>
/// GAP-1 (v2 audit, ADR-006): the in-memory <see cref="FakeBillingProvider"/> trusts a literal webhook
/// signature, so it must be registered ONLY in Development. Outside Development with no Stripe key,
/// <see cref="ServiceCollectionExtensions.AddInfrastructure"/> fails fast at startup rather than silently
/// wiring a provider that would accept forged, unauthenticated cross-tenant billing writes. Inspects the
/// registered descriptor; no provider is built, so nothing connects to Stripe or a DB.
/// </summary>
public class BillingProviderRegistrationTests
{
    [Fact]
    public void StripeKeyConfigured_SelectsStripe_EvenInProduction() =>
        Assert.Equal(typeof(StripeBillingProvider), ResolveImpl(Environments.Production, new()
        {
            ["Billing:Stripe:SecretKey"] = "sk_test_123",
        }));

    [Fact]
    public void NoStripeKey_InDevelopment_SelectsFake() =>
        Assert.Equal(typeof(FakeBillingProvider), ResolveImpl(Environments.Development, new()));

    [Fact]
    public void NoStripeKey_OutsideDevelopment_ThrowsAtStartup() =>
        Assert.Throws<InvalidOperationException>(() => ResolveImpl(Environments.Production, new()));

    // v3 DEP-10: when the deploy declares its expected Stripe mode, a mismatched key fails closed at startup.

    [Fact]
    public void ExpectLiveKey_ButTestKey_ThrowsAtStartup() =>
        Assert.Throws<InvalidOperationException>(() => ResolveImpl(Environments.Production, new()
        {
            ["Billing:Stripe:SecretKey"] = "sk_test_123",
            ["Billing:Stripe:ExpectLiveKey"] = "true",
        }));

    [Fact]
    public void ExpectTestKey_ButLiveKey_ThrowsAtStartup() =>
        Assert.Throws<InvalidOperationException>(() => ResolveImpl(Environments.Production, new()
        {
            ["Billing:Stripe:SecretKey"] = "sk_live_123",
            ["Billing:Stripe:ExpectLiveKey"] = "false",
        }));

    [Theory]
    [InlineData("sk_live_123", "true")]
    [InlineData("sk_test_123", "false")]
    public void ExpectedMode_MatchingKey_SelectsStripe(string key, string expectLive) =>
        Assert.Equal(typeof(StripeBillingProvider), ResolveImpl(Environments.Production, new()
        {
            ["Billing:Stripe:SecretKey"] = key,
            ["Billing:Stripe:ExpectLiveKey"] = expectLive,
        }));

    [Fact]
    public void NoExpectLiveKey_SkipsModeCheck_SelectsStripe() => // unset ⇒ no enforcement (local/dev)
        Assert.Equal(typeof(StripeBillingProvider), ResolveImpl(Environments.Production, new()
        {
            ["Billing:Stripe:SecretKey"] = "sk_test_123",
        }));

    private static Type? ResolveImpl(string environmentName, Dictionary<string, string?> settings)
    {
        // A dummy connection string so AddInfrastructure's DbContext registration doesn't need a real DB.
        settings["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=x;Username=x;Password=x";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(configuration, new FakeHostEnvironment(environmentName));

        return services.LastOrDefault(d => d.ServiceType == typeof(IBillingProvider))?.ImplementationType;
    }
}

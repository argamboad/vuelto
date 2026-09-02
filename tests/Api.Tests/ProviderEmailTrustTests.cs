using Microsoft.Extensions.Configuration;
using Perezosoft.Api.Services;

namespace Perezosoft.Api.Tests;

/// <summary>
/// The per-provider email-trust policy layered on the fail-closed claim check. It must trust
/// Microsoft's email ONLY on the <c>consumers</c> (personal MSA) tenant — where Microsoft verifies
/// the email but omits the claim — and fail closed for work/school tenants and any unknown provider,
/// preserving the MITI-3 takeover guard everywhere the risk is real.
/// </summary>
public class ProviderEmailTrustTests
{
    private static ProviderEmailTrust Policy(string? microsoftTenant = null)
    {
        var values = new Dictionary<string, string?>();
        if (microsoftTenant is not null)
            values["Authentication:Microsoft:Tenant"] = microsoftTenant;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ProviderEmailTrust(config);
    }

    [Fact]
    public void Google_IsTrusted()
    {
        // Google asserts email_verified anyway; trusting it is harmless and explicit.
        Assert.True(Policy().TrustsEmailWithoutClaim("google"));
    }

    [Theory]
    [InlineData(null)]          // unset ⇒ defaults to "consumers"
    [InlineData("consumers")]
    [InlineData("CONSUMERS")]   // case-insensitive
    public void Microsoft_IsTrusted_OnlyForConsumersTenant(string? tenant)
    {
        Assert.True(Policy(tenant).TrustsEmailWithoutClaim("microsoft"));
    }

    [Theory]
    [InlineData("organizations")]
    [InlineData("common")]
    [InlineData("00000000-0000-0000-0000-000000000000")] // a specific work/school tenant GUID
    public void Microsoft_FailsClosed_ForWorkSchoolTenants(string tenant)
    {
        Assert.False(Policy(tenant).TrustsEmailWithoutClaim("microsoft"));
    }

    [Theory]
    [InlineData("github")]
    [InlineData("facebook")]
    [InlineData("")]
    public void UnknownProvider_FailsClosed(string provider)
    {
        Assert.False(Policy().TrustsEmailWithoutClaim(provider));
    }
}

using Microsoft.Extensions.Configuration;

namespace Vuelto.Api.Services;

/// <summary>
/// Decides whether an OAuth provider's email may be treated as verified for the purpose of
/// auto-linking a new provider login to an existing same-email account, even when the provider does
/// not send an <c>email_verified</c> claim.
/// <para>
/// This sits ON TOP of the fail-closed <see cref="IClaimsExtractor.IsEmailVerified"/> (audit MITI-3):
/// the email-match merge proceeds only when the claim is explicitly <c>"true"</c> OR the provider is
/// trusted here. Unknown/unlisted providers always stay fail-closed. Microsoft omits the claim on its
/// v2 endpoint, but a PERSONAL account (the <c>consumers</c> tenant) has a Microsoft-verified email —
/// so Microsoft is trusted only for <c>consumers</c>. Work/school tenants
/// (<c>organizations</c>/<c>common</c>/a tenant GUID) can carry unverified aliases and remain
/// fail-closed automatically, so flipping the configured tenant reverts the trust without a code change.
/// </para>
/// </summary>
public interface IProviderEmailTrust
{
    bool TrustsEmailWithoutClaim(string provider);
}

public class ProviderEmailTrust(IConfiguration configuration) : IProviderEmailTrust
{
    public bool TrustsEmailWithoutClaim(string provider) => provider.ToLowerInvariant() switch
    {
        // Google always asserts email_verified, so this arm isn't normally exercised — listed for clarity.
        AuthProviders.Google => true,
        // Microsoft personal accounts (consumers) have a Microsoft-verified email; trust only there.
        AuthProviders.Microsoft => IsMicrosoftConsumersTenant(),
        // Unknown / future providers fail closed — the merge guard still requires the explicit claim.
        _ => false,
    };

    private bool IsMicrosoftConsumersTenant()
    {
        // Mirror the default in ServiceCollectionExtensions (unset ⇒ "consumers").
        var tenant = configuration["Authentication:Microsoft:Tenant"] ?? "consumers";
        return string.Equals(tenant, "consumers", StringComparison.OrdinalIgnoreCase);
    }
}

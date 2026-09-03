namespace Vuelto.Core.Mail;

/// <summary>
/// Drives the incremental OAuth mail-consent flow (EMAIL-2): build the provider authorization URL
/// (read-only mail scopes, biased to the signed-in account), protect/verify the round-trip state, and
/// exchange the returned code for tokens. The live code→token exchange and refresh talk to the IdP —
/// the manually-verified boundary; URL building and state are pure and unit-tested.
/// </summary>
public interface IMailConsentService
{
    /// <summary>The read-only authorization URL to send the browser to.</summary>
    string BuildAuthorizationUrl(string provider, string redirectUri, string state, string? loginHint = null);

    /// <summary>Tamper-proof, time-limited state carrying the initiating user + provider.</summary>
    string ProtectState(Guid userId, string provider);

    /// <summary>Verifies and decodes <see cref="ProtectState"/>; false if missing, tampered or expired.</summary>
    bool TryReadState(string? state, out Guid userId, out string provider);

    /// <summary>Live exchange of an auth code for tokens + account email (IdP boundary).</summary>
    Task<MailConsentTokens> ExchangeCodeAsync(string provider, string code, string redirectUri, CancellationToken cancellationToken = default);

    /// <summary>Refreshes an access token via the stored refresh token; providers may omit a new refresh token — the existing one is reused.</summary>
    Task<MailConsentTokens> RefreshAsync(string provider, string refreshToken, CancellationToken cancellationToken = default);
}

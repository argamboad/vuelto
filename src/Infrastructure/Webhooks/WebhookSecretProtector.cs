using Microsoft.AspNetCore.DataProtection;

namespace Perezosoft.Infrastructure.Webhooks;

/// <summary>
/// Encrypts/decrypts a webhook subscription's signing secret at rest (HOOKS, ADR-016), reusing the Data
/// Protection stack (keys already persisted to the DB — same approach as the MFA secret). The plaintext
/// secret is needed to HMAC-sign each delivery, so it can't be a one-way hash; encryption is the
/// defense-in-depth alternative.
/// </summary>
public interface IWebhookSecretProtector
{
    string Protect(string secret);
    string Unprotect(string encrypted);
}

public sealed class WebhookSecretProtector(IDataProtectionProvider dataProtection) : IWebhookSecretProtector
{
    // Purpose string feeds DataProtection key derivation — renaming it makes webhook secrets already
    // encrypted at rest undecryptable. Kept through the Perezosoft rename; bump only with a re-encrypt migration.
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Template.Webhook.Secret.v1");

    public string Protect(string secret) => _protector.Protect(secret);
    public string Unprotect(string encrypted) => _protector.Unprotect(encrypted);
}

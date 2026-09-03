using Microsoft.AspNetCore.DataProtection;
using Vuelto.Core.Mail;

namespace Vuelto.Infrastructure.Mail;

/// <summary>
/// EMAIL-2 (ADR-V016): OAuth tokens at rest ride the platform's Data Protection key ring (persisted in
/// the database, shared by every API instance) under a dedicated purpose — no separate encryption
/// secret to provision or rotate, and a payload from another purpose or key ring fails to unprotect.
/// </summary>
public sealed class DataProtectionEmailTokenProtector(IDataProtectionProvider provider) : IEmailTokenProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Vuelto.Mail.Tokens.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

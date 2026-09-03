using Vuelto.Core.Mail;

namespace Vuelto.Api.Tests.Mail;

/// <summary>Reversible stand-in for the Data Protection protector: "p:" + plaintext, so tests can assert what was stored without a key ring.</summary>
public sealed class FakeEmailTokenProtector : IEmailTokenProtector
{
    public string Protect(string plaintext) => "p:" + plaintext;

    public string Unprotect(string protectedValue) =>
        protectedValue.StartsWith("p:", StringComparison.Ordinal) ? protectedValue[2..] : throw new System.Security.Cryptography.CryptographicException("not protected by this fake");
}

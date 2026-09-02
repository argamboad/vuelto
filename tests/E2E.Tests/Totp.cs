using OtpNet;

namespace Perezosoft.E2E.Tests;

/// <summary>Computes the current TOTP for a Base32 secret — the same OtpNet the API uses (MFA-1).</summary>
public static class Totp
{
    public static string Now(string base32Secret) =>
        new OtpNet.Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp();
}

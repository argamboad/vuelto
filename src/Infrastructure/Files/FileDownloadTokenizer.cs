using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Vuelto.Core.Abstractions;

namespace Vuelto.Infrastructure.Files;

/// <summary>
/// Signs local-disk download tokens with the Data Protection stack's
/// <see cref="ITimeLimitedDataProtector"/> (ADR-010) — keys are persisted to the DB, so tokens stay
/// valid across restarts/redeploys. The payload is <c>{tenantId}/{key}</c>; a Guid never contains
/// <c>/</c>, so the first <c>/</c> splits tenant from key (and the key keeps its own slashes). The
/// protector encrypts + MACs, so an expired or altered token fails to unprotect and is rejected.
/// </summary>
public sealed class FileDownloadTokenizer(IDataProtectionProvider provider) : IFileDownloadTokenizer
{
    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector("Template.Files.Download.v1").ToTimeLimitedDataProtector();

    public string Mint(Guid tenantId, string key, TimeSpan lifetime) =>
        _protector.Protect($"{tenantId:D}/{key}", lifetime);

    public bool TryRead(string token, out Guid tenantId, out string key)
    {
        tenantId = default;
        key = "";
        if (string.IsNullOrEmpty(token))
            return false;

        try
        {
            var payload = _protector.Unprotect(token); // throws if expired or tampered
            var slash = payload.IndexOf('/');
            if (slash <= 0 || !Guid.TryParse(payload[..slash], out tenantId))
                return false;

            key = payload[(slash + 1)..];
            return key.Length > 0;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

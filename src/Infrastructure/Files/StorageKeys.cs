using Perezosoft.Core.Abstractions;

namespace Perezosoft.Infrastructure.Files;

/// <summary>
/// Shared tenant-scoped key handling for the file-storage backends (ADR-010). One place validates the
/// caller's logical key and namespaces it under the tenant, so local disk and S3 enforce the same
/// isolation rules. Rejects empty, backslash, rooted, and <c>.</c>/<c>..</c> keys — a client path is
/// never trusted.
/// </summary>
internal static class StorageKeys
{
    public static void Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidStorageKeyException("Storage key must not be empty.");
        if (key.Contains('\\'))
            throw new InvalidStorageKeyException("Storage key must use '/' separators, not '\\'.");
        if (Path.IsPathRooted(key))
            throw new InvalidStorageKeyException("Storage key must be a relative path.");

        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidStorageKeyException("Storage key must reference a file.");
        if (segments.Any(s => s is "." or ".."))
            throw new InvalidStorageKeyException("Storage key must not contain '.' or '..' segments.");
    }

    /// <summary>The validated key namespaced under the tenant: <c>{tenantId}/{key}</c>.</summary>
    public static string ForTenant(Guid tenantId, string key)
    {
        Validate(key);
        return $"{tenantId:D}/{key}";
    }
}

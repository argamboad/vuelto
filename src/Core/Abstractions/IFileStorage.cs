namespace Vuelto.Core.Abstractions;

/// <summary>
/// The single seam for storing and retrieving binary content (ADR-010). Mirrors the
/// <see cref="IEmailSender"/> shape: a Core abstraction with a config-selected Infrastructure impl
/// (local disk for dev, an S3-compatible backend in production). Features depend on this — never on a
/// cloud SDK or <c>System.IO</c> directly.
/// <para>
/// <b>Keys are tenant-scoped.</b> The implementation namespaces every object under the current tenant
/// (<c>{tenantId}/…</c>) and <b>rejects</b> keys that try to escape that namespace (traversal,
/// rooted/absolute paths) — the blob analogue of the <c>ITenantScoped</c> query filter (ADR-003). With
/// no current tenant the operation fails closed. <b>Streams, never buffers</b> whole files.
/// </para>
/// </summary>
public interface IFileStorage
{
    /// <summary>Stores (overwriting) the object at <paramref name="key"/> with the given content type.</summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Opens the object for reading, or null if it does not exist. The caller disposes the result.</summary>
    Task<FileObject?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>True if the object exists for the current tenant.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object. Deleting a missing key is a no-op, not an error (cloud parity).</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// A signed, time-limited URL that downloads exactly this object (ADR-010). For cloud backends this
    /// is a native presigned GET URL; for local disk it points at the platform <c>GET /api/files/{token}</c>
    /// endpoint with an <c>ITimeLimitedDataProtector</c>-signed token. The URL is scoped to one key and
    /// expires after <paramref name="lifetime"/> — never a directory or wildcard.
    /// </summary>
    Task<Uri> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mints and reads the opaque, signed, time-limited tokens that back local-disk download URLs
/// (ADR-010). The token binds a tenant + key + expiry; reading fails closed for an expired, tampered,
/// or malformed token. One implementation owns the token format so the minting side (storage) and the
/// reading side (the <c>/api/files/{token}</c> endpoint) can't drift.
/// </summary>
public interface IFileDownloadTokenizer
{
    string Mint(Guid tenantId, string key, TimeSpan lifetime);

    /// <summary>True (with <paramref name="tenantId"/>/<paramref name="key"/> set) for a valid, unexpired token.</summary>
    bool TryRead(string token, out Guid tenantId, out string key);
}

/// <summary>
/// A readable handle to a stored object. Own it with <c>await using</c> — disposing releases the
/// underlying stream (and any file handle).
/// </summary>
public sealed record FileObject(Stream Content, string ContentType, long Length) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>Thrown when a storage key is not valid for the current tenant (empty, traversal, rooted, etc.).</summary>
public sealed class InvalidStorageKeyException(string message) : Exception(message);

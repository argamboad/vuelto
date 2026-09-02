using Microsoft.Extensions.Options;
using Vuelto.Core.Abstractions;

namespace Vuelto.Infrastructure.Files;

/// <summary>
/// Local-filesystem <see cref="IFileStorage"/> — the dev/test default (ADR-010). Objects live under
/// <c>{root}/blobs/{tenantId}/{key}</c>; the content type is kept in a parallel
/// <c>{root}/meta/{tenantId}/{key}</c> tree (two trees so a user key can never collide with another
/// object's metadata). Keys are validated server-side and resolved paths are asserted to stay within
/// the tenant directory, so a crafted key can't read or write outside the tenant namespace. Streams
/// to/from disk — never buffers a whole file. With no current tenant it fails closed.
/// </summary>
public sealed class LocalDiskFileStorage(
    IOptions<LocalFileStorageSettings> options,
    ICurrentTenant currentTenant,
    IFileDownloadTokenizer tokenizer) : IFileStorage
{
    private readonly string _root = ResolveRoot(options.Value.RootPath);
    private readonly string _downloadBaseUrl = options.Value.DownloadBaseUrl;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var (blob, meta) = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        Directory.CreateDirectory(Path.GetDirectoryName(meta)!);

        await using (var file = new FileStream(blob, FileMode.Create, FileAccess.Write, FileShare.None))
            await content.CopyToAsync(file, cancellationToken);

        await File.WriteAllTextAsync(meta, string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, cancellationToken);
    }

    public Task<FileObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var (blob, meta) = Resolve(key);
        if (!File.Exists(blob))
            return Task.FromResult<FileObject?>(null);

        var contentType = File.Exists(meta) ? File.ReadAllText(meta) : "application/octet-stream";
        var stream = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<FileObject?>(new FileObject(stream, contentType, stream.Length));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var (blob, _) = Resolve(key);
        return Task.FromResult(File.Exists(blob));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var (blob, meta) = Resolve(key);
        if (File.Exists(blob)) File.Delete(blob);
        if (File.Exists(meta)) File.Delete(meta);
        return Task.CompletedTask;
    }

    public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var tenantId = currentTenant.TenantId
            ?? throw new InvalidOperationException("No current tenant — file storage requires a tenant context.");
        StorageKeys.Validate(key); // don't mint a URL for a key we'd refuse to serve

        var token = tokenizer.Mint(tenantId, key, lifetime);
        var relative = $"api/files/{token}";
        var uri = string.IsNullOrWhiteSpace(_downloadBaseUrl)
            ? new Uri(relative, UriKind.Relative)
            : new Uri(new Uri(_downloadBaseUrl, UriKind.Absolute), relative);
        return Task.FromResult(uri);
    }

    private static string ResolveRoot(string configured) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "storage")
            : configured);

    /// <summary>
    /// Maps a tenant-scoped logical key to its absolute blob + meta paths, failing closed without a
    /// tenant and rejecting any key that would escape the tenant's directory.
    /// </summary>
    private (string blob, string meta) Resolve(string key)
    {
        var tenantId = currentTenant.TenantId
            ?? throw new InvalidOperationException("No current tenant — file storage requires a tenant context.");

        StorageKeys.Validate(key);

        var tenantBlobRoot = Path.Combine(_root, "blobs", tenantId.ToString());
        var tenantMetaRoot = Path.Combine(_root, "meta", tenantId.ToString());
        var blob = Path.GetFullPath(Path.Combine(tenantBlobRoot, key));
        var meta = Path.GetFullPath(Path.Combine(tenantMetaRoot, key));

        // Defense in depth on top of ValidateKey: the resolved path must stay under the tenant root.
        if (!IsWithin(blob, tenantBlobRoot) || !IsWithin(meta, tenantMetaRoot))
            throw new InvalidStorageKeyException($"Key '{key}' resolves outside the tenant namespace.");

        return (blob, meta);
    }

    private static bool IsWithin(string candidate, string root)
    {
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(rootFull, StringComparison.Ordinal);
    }
}

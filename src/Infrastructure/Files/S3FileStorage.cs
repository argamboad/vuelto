using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Vuelto.Core.Abstractions;

namespace Vuelto.Infrastructure.Files;

/// <summary>
/// S3-compatible <see cref="IFileStorage"/> (ADR-010) — the production backend, selected when
/// <c>Storage:S3</c> is configured. Works against AWS S3, MinIO, Cloudflare R2, Backblaze B2, etc. via
/// the endpoint/region/path-style settings. Object keys are tenant-namespaced and validated by
/// <see cref="StorageKeys"/> exactly like local disk; <see cref="GetDownloadUrlAsync"/> returns a
/// <b>native presigned GET URL</b>, so the <c>/api/files/{token}</c> endpoint is the local-disk path
/// only. Streams to/from S3; fails closed without a tenant.
/// </summary>
public sealed class S3FileStorage(IAmazonS3 s3, ICurrentTenant currentTenant, IOptions<S3StorageSettings> options, TimeProvider clock) : IFileStorage
{
    private readonly string _bucket = options.Value.Bucket;

    // Presigned URLs default to HTTPS; honor a plain-HTTP endpoint (e.g. local MinIO). The scheme is
    // not part of the signature, so this is safe.
    private readonly Protocol _protocol =
        options.Value.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTP
            : Protocol.HTTPS;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = ObjectKey(key),
            InputStream = content,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            AutoCloseStream = false, // the caller owns the input stream
        }, cancellationToken);
    }

    public async Task<FileObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(_bucket, ObjectKey(key), cancellationToken);
            var contentType = string.IsNullOrWhiteSpace(response.Headers.ContentType)
                ? "application/octet-stream"
                : response.Headers.ContentType;
            return new FileObject(response.ResponseStream, contentType, response.ContentLength);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.GetObjectMetadataAsync(_bucket, ObjectKey(key), cancellationToken);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        s3.DeleteObjectAsync(_bucket, ObjectKey(key), cancellationToken); // delete-missing is a no-op in S3

    public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var url = s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = ObjectKey(key),
            Verb = HttpVerb.GET,
            Protocol = _protocol,
            Expires = clock.GetUtcNow().UtcDateTime.Add(lifetime), // injected clock (v2 audit GAP-4)
        });
        return Task.FromResult(new Uri(url));
    }

    private string ObjectKey(string key)
    {
        var tenantId = currentTenant.TenantId
            ?? throw new InvalidOperationException("No current tenant — file storage requires a tenant context.");
        return StorageKeys.ForTenant(tenantId, key);
    }

    /// <summary>Builds an S3 client for AWS or any S3-compatible endpoint (MinIO/R2/B2) from settings.</summary>
    public static IAmazonS3 CreateClient(S3StorageSettings settings)
    {
        var config = new AmazonS3Config { ForcePathStyle = settings.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            config.ServiceURL = settings.Endpoint;
            config.AuthenticationRegion = string.IsNullOrWhiteSpace(settings.Region) ? "us-east-1" : settings.Region;
        }
        else if (!string.IsNullOrWhiteSpace(settings.Region))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(settings.Region);
        }

        return new AmazonS3Client(new BasicAWSCredentials(settings.AccessKey, settings.SecretKey), config);
    }
}

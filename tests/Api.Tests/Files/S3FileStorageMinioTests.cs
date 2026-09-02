using System.Text;
using Microsoft.Extensions.Options;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure.Files;
using Testcontainers.Minio;

namespace Vuelto.Api.Tests.Files;

/// <summary>
/// Starts one MinIO (S3-compatible) container for the class and creates the test bucket. Proves the
/// same <see cref="S3FileStorage"/> code targets a non-AWS endpoint via <c>ForcePathStyle</c> + a
/// custom service URL — the exact shape used for MinIO staging / R2 / B2.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string Bucket = "vuelto-test";
    private readonly MinioContainer _minio = new MinioBuilder("minio/minio:latest").Build();

    public S3StorageSettings Settings { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();
        Settings = new S3StorageSettings
        {
            Bucket = Bucket,
            Endpoint = _minio.GetConnectionString(),
            Region = "us-east-1",
            AccessKey = _minio.GetAccessKey(),
            SecretKey = _minio.GetSecretKey(),
            ForcePathStyle = true,
        };

        using var admin = S3FileStorage.CreateClient(Settings);
        await admin.PutBucketAsync(Bucket);
    }

    public Task DisposeAsync() => _minio.DisposeAsync().AsTask();
}

/// <summary>
/// FILES-3 (ADR-010): <see cref="S3FileStorage"/> round-trips against real S3 semantics (MinIO),
/// keeps keys tenant-namespaced/isolated, hands out a working native presigned URL, and honors the
/// same key validation as local disk.
/// </summary>
public sealed class S3FileStorageMinioTests(MinioFixture fixture) : IClassFixture<MinioFixture>
{
    private S3FileStorage NewStorage(Guid tenant) =>
        new(S3FileStorage.CreateClient(fixture.Settings),
            new TestCurrentTenant { TenantId = tenant },
            Options.Create(fixture.Settings),
            TimeProvider.System);

    [Fact]
    public async Task RoundTrip_PutExistsGetDelete()
    {
        var storage = NewStorage(Guid.NewGuid());
        var bytes = Encoding.UTF8.GetBytes("s3 hello");

        await storage.PutAsync("docs/a.txt", new MemoryStream(bytes), "text/plain");
        Assert.True(await storage.ExistsAsync("docs/a.txt"));

        await using (var file = await storage.GetAsync("docs/a.txt"))
        {
            Assert.NotNull(file);
            Assert.Equal("text/plain", file!.ContentType);
            Assert.Equal(bytes, await ReadAllAsync(file.Content));
        }

        await storage.DeleteAsync("docs/a.txt");
        Assert.False(await storage.ExistsAsync("docs/a.txt"));
        Assert.Null(await storage.GetAsync("docs/a.txt"));
    }

    [Fact]
    public async Task TwoTenants_SameKey_AreIsolated()
    {
        var a = NewStorage(Guid.NewGuid());
        var b = NewStorage(Guid.NewGuid());

        await a.PutAsync("shared", Bytes("A"), "text/plain");
        await b.PutAsync("shared", Bytes("B"), "text/plain");

        await using var fa = await a.GetAsync("shared");
        await using var fb = await b.GetAsync("shared");
        Assert.Equal("A", Encoding.UTF8.GetString(await ReadAllAsync(fa!.Content)));
        Assert.Equal("B", Encoding.UTF8.GetString(await ReadAllAsync(fb!.Content)));
    }

    [Fact]
    public async Task PresignedUrl_DownloadsTheObject()
    {
        var storage = NewStorage(Guid.NewGuid());
        await storage.PutAsync("p.txt", Bytes("presigned-body"), "text/plain");

        var url = await storage.GetDownloadUrlAsync("p.txt", TimeSpan.FromMinutes(5));

        Assert.True(url.IsAbsoluteUri);
        using var http = new HttpClient();
        Assert.Equal("presigned-body", await http.GetStringAsync(url));
    }

    [Fact]
    public async Task Delete_MissingKey_IsNoOp()
    {
        await NewStorage(Guid.NewGuid()).DeleteAsync("ghost"); // must not throw
    }

    [Fact]
    public async Task InvalidKey_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidStorageKeyException>(
            () => NewStorage(Guid.NewGuid()).PutAsync("../escape", Bytes("x"), "text/plain"));
    }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }
}

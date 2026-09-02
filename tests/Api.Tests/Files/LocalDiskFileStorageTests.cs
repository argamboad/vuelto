using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Abstractions;
using Perezosoft.Infrastructure.Files;

namespace Perezosoft.Api.Tests.Files;

/// <summary>
/// FILES-1 (ADR-010): <see cref="LocalDiskFileStorage"/> round-trips bytes + content-type, namespaces
/// every object under the current tenant, and rejects keys that try to escape that namespace
/// (traversal/rooted) or run without a tenant (fail closed). Pure filesystem — a throwaway temp root,
/// no database.
/// </summary>
public sealed class LocalDiskFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tmpl-files-" + Guid.NewGuid().ToString("N"));

    private LocalDiskFileStorage NewStorage(Guid? tenantId, string downloadBaseUrl = "") =>
        new(Options.Create(new LocalFileStorageSettings { RootPath = _root, DownloadBaseUrl = downloadBaseUrl }),
            new TestCurrentTenant { TenantId = tenantId },
            new FileDownloadTokenizer(new EphemeralDataProtectionProvider()));

    [Fact]
    public async Task PutThenGet_RoundTripsBytesAndContentType()
    {
        var storage = NewStorage(Guid.NewGuid());
        var bytes = Encoding.UTF8.GetBytes("hello world");

        await storage.PutAsync("avatars/me.png", new MemoryStream(bytes), "image/png");

        await using var file = await storage.GetAsync("avatars/me.png");
        Assert.NotNull(file);
        Assert.Equal("image/png", file!.ContentType);
        Assert.Equal(bytes.Length, file.Length);
        Assert.Equal(bytes, await ReadAllAsync(file.Content));
    }

    [Fact]
    public async Task Exists_ReflectsPutAndDelete()
    {
        var storage = NewStorage(Guid.NewGuid());
        Assert.False(await storage.ExistsAsync("doc.txt"));

        await storage.PutAsync("doc.txt", Bytes("x"), "text/plain");
        Assert.True(await storage.ExistsAsync("doc.txt"));

        await storage.DeleteAsync("doc.txt");
        Assert.False(await storage.ExistsAsync("doc.txt"));
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsNull()
    {
        var storage = NewStorage(Guid.NewGuid());
        Assert.Null(await storage.GetAsync("nope.bin"));
    }

    [Fact]
    public async Task Delete_MissingKey_IsNoOp()
    {
        var storage = NewStorage(Guid.NewGuid());
        await storage.DeleteAsync("nope.bin"); // must not throw
    }

    [Fact]
    public async Task Put_OverwritesExisting()
    {
        var storage = NewStorage(Guid.NewGuid());
        await storage.PutAsync("k", Bytes("first"), "text/plain");
        await storage.PutAsync("k", Bytes("second"), "text/markdown");

        await using var file = await storage.GetAsync("k");
        Assert.Equal("text/markdown", file!.ContentType);
        Assert.Equal("second", Encoding.UTF8.GetString(await ReadAllAsync(file.Content)));
    }

    [Fact]
    public async Task TwoTenants_SameKey_AreIsolated()
    {
        var a = NewStorage(Guid.NewGuid());
        var b = NewStorage(Guid.NewGuid());

        await a.PutAsync("shared/key", Bytes("A"), "text/plain");
        await b.PutAsync("shared/key", Bytes("B"), "text/plain");

        await using var fa = await a.GetAsync("shared/key");
        await using var fb = await b.GetAsync("shared/key");
        Assert.Equal("A", Encoding.UTF8.GetString(await ReadAllAsync(fa!.Content)));
        Assert.Equal("B", Encoding.UTF8.GetString(await ReadAllAsync(fb!.Content)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("a/../../b")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("C:\\data\\x")]
    [InlineData("a/./../../b")]
    public async Task InvalidKeys_AreRejected(string key)
    {
        var storage = NewStorage(Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidStorageKeyException>(
            () => storage.PutAsync(key, Bytes("x"), "text/plain"));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(
            () => storage.GetAsync(key));
    }

    [Fact]
    public async Task NoTenant_FailsClosed()
    {
        var storage = NewStorage(tenantId: null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.PutAsync("k", Bytes("x"), "text/plain"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetAsync("k"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetDownloadUrlAsync("k", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task GetDownloadUrl_BuildsApiFilesUrl_Relative_AndAbsolute()
    {
        var relative = await NewStorage(Guid.NewGuid()).GetDownloadUrlAsync("a/b.png", TimeSpan.FromMinutes(5));
        Assert.False(relative.IsAbsoluteUri);
        Assert.StartsWith("api/files/", relative.ToString());

        var absolute = await NewStorage(Guid.NewGuid(), "https://api.example.com")
            .GetDownloadUrlAsync("a/b.png", TimeSpan.FromMinutes(5));
        Assert.True(absolute.IsAbsoluteUri);
        Assert.Equal("https", absolute.Scheme);
        Assert.StartsWith("/api/files/", absolute.AbsolutePath);
    }

    [Fact]
    public async Task GetDownloadUrl_RejectsInvalidKey()
    {
        await Assert.ThrowsAsync<InvalidStorageKeyException>(
            () => NewStorage(Guid.NewGuid()).GetDownloadUrlAsync("../escape", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task TraversalKey_CannotEscapeTenantRoot_NoFileWrittenOutside()
    {
        var storage = NewStorage(Guid.NewGuid());
        var outside = Path.Combine(_root, "escaped.txt");

        await Assert.ThrowsAsync<InvalidStorageKeyException>(
            () => storage.PutAsync("../../escaped.txt", Bytes("pwn"), "text/plain"));

        Assert.False(File.Exists(outside));
    }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

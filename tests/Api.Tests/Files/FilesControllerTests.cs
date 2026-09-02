using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Perezosoft.Api.Controllers;
using Perezosoft.Api.Services;
using Perezosoft.Infrastructure.Files;

namespace Perezosoft.Api.Tests.Files;

/// <summary>
/// FILES-2 (ADR-010): <see cref="FilesController"/> streams a file for a valid token (entering the
/// token's tenant so storage scopes correctly) and returns <b>404</b> for anything else — expired,
/// tampered, or wrong-tenant tokens, and missing objects — never leaking what exists. Local-disk +
/// a shared tenant context; no database.
/// </summary>
public sealed class FilesControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tmpl-filesctl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValidToken_StreamsFileWithContentType()
    {
        var (storage, ctx, tokenizer) = Build();
        var tenant = Guid.NewGuid();
        using (ctx.EnterTenant(tenant))
            await storage.PutAsync("docs/readme.txt", Bytes("hello"), "text/plain");
        var token = tokenizer.Mint(tenant, "docs/readme.txt", TimeSpan.FromMinutes(5));

        var result = await new FilesController(storage, ctx, tokenizer).Download(token, default);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("text/plain", file.ContentType);
        await using var stream = file.FileStream; // framework disposes in prod; do it here to free the handle
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal("hello", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task ValidToken_SetsAttachmentFilenameFromKeyBasename()
    {
        var (storage, ctx, tokenizer) = Build();
        var tenant = Guid.NewGuid();
        using (ctx.EnterTenant(tenant))
            await storage.PutAsync("exports/2026-bundle.json", Bytes("{}"), "application/json");
        var token = tokenizer.Mint(tenant, "exports/2026-bundle.json", TimeSpan.FromMinutes(5));

        var result = await new FilesController(storage, ctx, tokenizer).Download(token, default);

        // Content-Disposition: attachment — a browser downloads instead of rendering the JSON
        // inline, and a same-tab navigation to the URL never replaces the page (NATIVE-3).
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("2026-bundle.json", file.FileDownloadName);
        await using var _ = file.FileStream;
    }

    [Fact]
    public async Task ExpiredToken_Returns404()
    {
        var (storage, ctx, tokenizer) = Build();
        var tenant = Guid.NewGuid();
        using (ctx.EnterTenant(tenant))
            await storage.PutAsync("k", Bytes("x"), "text/plain");
        var token = tokenizer.Mint(tenant, "k", TimeSpan.FromSeconds(-1));

        Assert.IsType<NotFoundResult>(await new FilesController(storage, ctx, tokenizer).Download(token, default));
    }

    [Fact]
    public async Task GarbageToken_Returns404()
    {
        var (storage, ctx, tokenizer) = Build();
        Assert.IsType<NotFoundResult>(await new FilesController(storage, ctx, tokenizer).Download("nonsense", default));
    }

    [Fact]
    public async Task ValidTokenButMissingFile_Returns404()
    {
        var (storage, ctx, tokenizer) = Build();
        var token = tokenizer.Mint(Guid.NewGuid(), "ghost", TimeSpan.FromMinutes(5));
        Assert.IsType<NotFoundResult>(await new FilesController(storage, ctx, tokenizer).Download(token, default));
    }

    [Fact]
    public async Task TokenForAnotherTenant_DoesNotLeak()
    {
        var (storage, ctx, tokenizer) = Build();
        var owner = Guid.NewGuid();
        using (ctx.EnterTenant(owner))
            await storage.PutAsync("secret", Bytes("owner-data"), "text/plain");

        // A token scoped to a DIFFERENT tenant for the same key resolves under that tenant — which has
        // no such file — so the owner's file is never served.
        var token = tokenizer.Mint(Guid.NewGuid(), "secret", TimeSpan.FromMinutes(5));
        Assert.IsType<NotFoundResult>(await new FilesController(storage, ctx, tokenizer).Download(token, default));
    }

    private (LocalDiskFileStorage storage, HttpCurrentTenant ctx, FileDownloadTokenizer tokenizer) Build()
    {
        var ctx = new HttpCurrentTenant(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var tokenizer = new FileDownloadTokenizer(new EphemeralDataProtectionProvider());
        var storage = new LocalDiskFileStorage(
            Options.Create(new LocalFileStorageSettings { RootPath = _root }), ctx, tokenizer);
        return (storage, ctx, tokenizer);
    }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

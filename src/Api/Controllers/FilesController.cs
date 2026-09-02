using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vuelto.Core.Abstractions;

namespace Vuelto.Api.Controllers;

/// <summary>
/// Serves local-disk file downloads behind a signed, time-limited token (FILES-2, ADR-010). A
/// <b>system</b> endpoint — the token <b>is</b> the authorization, so it's anonymous and does not
/// extend <c>TenantApiControllerBase</c>. The token binds tenant + key + expiry; this endpoint reads
/// it, <b>enters that tenant context</b> (ADR-003) so the storage layer scopes correctly, and streams
/// the one object. Any failure to read the token, or a missing object, returns <b>404</b> — never a
/// hint about what exists. (Cloud backends hand out native presigned URLs and bypass this endpoint.)
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/files")]
public class FilesController(
    IFileStorage storage,
    ITenantContext tenantContext,
    IFileDownloadTokenizer tokenizer) : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Download(string token, CancellationToken cancellationToken)
    {
        if (!tokenizer.TryRead(token, out var tenantId, out var key))
            return NotFound();

        FileObject? file;
        using (tenantContext.EnterTenant(tenantId))
            file = await storage.GetAsync(key, cancellationToken);

        if (file is null)
            return NotFound();

        // The framework streams and disposes file.Content; the entered-tenant scope is already closed
        // (the open handle is self-contained, so streaming doesn't need the tenant context).
        // The filename overload sets Content-Disposition: attachment (named by the key's basename,
        // server-controlled) — browsers download instead of rendering inline, a same-tab navigation
        // never replaces the page, and native clients read the filename from the header (NATIVE-3).
        return File(file.Content, file.ContentType, Path.GetFileName(key));
    }
}

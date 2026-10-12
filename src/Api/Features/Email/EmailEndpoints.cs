using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Vuelto.Api.Authentication;
using Vuelto.Api.Configuration;
using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;
using Vuelto.Core.Mail;
using Vuelto.Core.Vouchers;

namespace Vuelto.Api.Features.Email;

/// <summary>
/// EMAIL-2/3 routes under <c>/api/email/connections</c>. User-scoped: a caller only ever sees or edits
/// their own connections, tokens are never returned, and a connection is created <b>only</b> through the
/// consent round-trip (GET authorize → IdP → GET callback). The callback is the one anonymous endpoint
/// in the group: like the platform's <c>/api/files/{token}</c>, the signed, time-limited state IS its
/// authorization (ADR-V016). Unknown or another user's id → uniform 404.
/// </summary>
public static class EmailEndpoints
{
    public const string CallbackPath = "/api/email/connections/callback";

    public static IEndpointRouteBuilder MapEmail(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/email/connections");

        group.MapGet("/", async (ClaimsPrincipal user, EmailConnectionHandler handler, IEnumerable<IEmailReader> readers, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            var list = await handler.ListAsync(uid, ct);
            foreach (var c in list) await handler.BackfillFolderNamesAsync(c, readers, ct); // legacy rows: ids → names, once
            return Results.Ok(list.Select(EmailConnectionResponse.From).ToList());
        });

        // GET /authorize?provider=microsoft|google → the read-only consent URL, biased to the signed-in account.
        group.MapGet("/authorize", (ClaimsPrincipal user, [FromQuery] string? provider, HttpRequest request, IMailConsentService consent, MailConsentSettings settings) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            var p = EmailProviders.Normalize(provider);
            if (p is null) return Results.BadRequest(new ErrorResponse("invalid_provider", "Provider must be 'microsoft' or 'google'."));
            if (!settings.IsConfigured(p))
                return Results.BadRequest(new ErrorResponse("provider_not_configured", $"Mail consent for '{p}' is not configured on this server."));
            var state = consent.ProtectState(uid, p);
            var url = consent.BuildAuthorizationUrl(p, CallbackUri(request), state, user.FindFirstValue(ClaimTypes.Email));
            return Results.Ok(new AuthorizeResponse(url));
        });

        // GET /callback — the OAuth redirect target. Anonymous: identified by the signed state, not a JWT.
        group.MapGet("/callback", async (
            [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, HttpRequest request,
            IMailConsentService consent, EmailConnectionHandler handler, IApplicationSettings app, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("Vuelto.Email.Consent");
            var back = $"{app.ClientUrl}/email";
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || !consent.TryReadState(state, out var uid, out var provider))
                return Results.Redirect($"{back}?email_error=consent_failed");

            try
            {
                var tokens = await consent.ExchangeCodeAsync(provider, code, CallbackUri(request), ct);
                // Pre-seed the verified voucher senders + subjects so the new connection is immediately useful.
                var (created, failure) = await handler.CreateAsync(uid, new NewEmailConnection(
                    provider, tokens.AccountEmail, tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt,
                    KnownVoucherSenders.All.ToArray(), BankVoucherMap.Default.SubjectFilters.ToArray()), ct);
                if (created is null)
                    return Results.Redirect($"{back}?email_error={(failure?.Error == "connection_exists" ? "already_connected" : "consent_failed")}");
                return Results.Redirect($"{back}?connected={provider}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Mail consent callback failed for provider {Provider}", provider);
                return Results.Redirect($"{back}?email_error=consent_failed");
            }
        }).AllowAnonymous();

        group.MapGet("/suggested-filters", () => Results.Ok(new SuggestedFiltersResponse(
            KnownVoucherSenders.All.ToArray(),
            BankVoucherMap.Default.SubjectFilters.ToArray(),
            [
                new BankFilterPreset("BAC", [KnownVoucherSenders.Bac], ["Notificación de transacción"]),
                new BankFilterPreset("BN", [KnownVoucherSenders.BN], ["Voucher Digital", "BN Conectividad le informa"]),
            ])));

        // POST / is refused on purpose: tokens arrive only through the consent callback (never a client body).
        group.MapPost("/", (ClaimsPrincipal user) =>
            user.GetUserId() is null ? Results.Unauthorized()
                : Results.BadRequest(new ErrorResponse("use_consent_flow", "Email connections are created through the OAuth consent flow — start with GET /authorize.")));

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, EmailConnectionHandler handler, IEnumerable<IEmailReader> readers, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            var c = await handler.GetAsync(uid, id, ct);
            if (c is null) return NotFound();
            await handler.BackfillFolderNamesAsync(c, readers, ct);
            return Results.Ok(EmailConnectionResponse.From(c));
        });

        // GET /{id}/folders — the account's real folders/labels for the picker (409 needs_reconsent on a dead token).
        group.MapGet("/{id:guid}/folders", async (Guid id, ClaimsPrincipal user, EmailConnectionHandler handler, IEnumerable<IEmailReader> readers, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            var c = await handler.GetAsync(uid, id, ct);
            if (c is null) return NotFound();
            var reader = readers.FirstOrDefault(r => r.Provider == c.Provider);
            if (reader is null) return Results.BadRequest(new ErrorResponse("unsupported_provider", "No reader for this provider."));
            var result = await reader.ListFoldersAsync(c, ct);
            return result.NeedsReconsent
                ? Results.Conflict(new ErrorResponse("needs_reconsent", "Reconnect this inbox to list folders."))
                : Results.Ok(result.Folders.Select(f => new FolderResponse(f.Id, f.Name)).ToList());
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateEmailConnectionRequest request, ClaimsPrincipal user, EmailConnectionHandler handler, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            var (c, failure) = await handler.UpdateAsync(uid, id, request, ct);
            if (failure is not null) return Results.BadRequest(failure);
            return c is null ? NotFound() : Results.Ok(EmailConnectionResponse.From(c));
        });

        // POST /sync — "Sync all inboxes" (the Review queue's button): every connection of the caller, one summary.
        group.MapPost("/sync", async (ClaimsPrincipal user, EmailConnectionHandler handler, IVoucherStagingService staging, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            return Results.Ok(await handler.SyncAllAsync(uid, staging, ct));
        });

        // POST /{id}/sync — "Sync now": stage this connection's matching mail immediately (EMAIL-4).
        group.MapPost("/{id:guid}/sync", async (Guid id, ClaimsPrincipal user, EmailConnectionHandler handler, IVoucherStagingService staging, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            var c = await handler.GetAsync(uid, id, ct);
            if (c is null) return NotFound();
            var result = await staging.StageConnectionAsync(c, ct);
            return result.NeedsReconsent
                ? Results.Conflict(new ErrorResponse("needs_reconsent", "Reconnect this inbox to sync."))
                : Results.Ok(new SyncResultResponse(result.Staged, result.Duplicates, result.Unrecognized));
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, EmailConnectionHandler handler, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            return await handler.DeleteAsync(uid, id, ct) ? Results.NoContent() : NotFound();
        });

        return app;
    }

    private static IResult NotFound() => Results.NotFound(new ErrorResponse("not_found", "email connection not found"));

    /// <summary>The redirect_uri registered with the IdP — the API's own callback (scheme/host as the request saw them).</summary>
    private static string CallbackUri(HttpRequest request) => $"{request.Scheme}://{request.Host}{CallbackPath}";
}

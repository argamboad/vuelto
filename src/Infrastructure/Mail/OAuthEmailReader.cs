using System.Net;
using Microsoft.Extensions.Logging;
using Vuelto.Core.Entities;
using Vuelto.Core.Mail;
using Vuelto.Core.Repositories;
using Vuelto.Core.Vouchers;

namespace Vuelto.Infrastructure.Mail;

/// <summary>Provider fetch outcome: the matched messages plus whether the page cap was hit (more may exist).</summary>
public record QueryResult(IReadOnlyList<VoucherMessage> Messages, bool Saturated);

/// <summary>
/// Shared OAuth orchestration for the provider readers (EMAIL-3): unprotect the access token, run the
/// provider query, and on a 401 refresh once and retry; a connection that still fails is flagged
/// <c>needs_reconsent</c> and skipped — never thrown out of the poll loop. Transient 429/5xx errors skip
/// this poll without advancing the cursor; any other HTTP failure flags the connection so the user sees
/// it needs attention. Subclasses implement only the provider-specific calls.
/// </summary>
public abstract class OAuthEmailReader(IEmailTokenProtector tokens, IMailConsentService consent, IRepository<EmailConnection> connections, TimeProvider clock, ILogger logger) : IEmailReader
{
    protected ILogger Logger => logger;

    public abstract string Provider { get; }

    /// <summary>Provider-specific fetch + map. Throws <see cref="MailUnauthorizedException"/> on 401.</summary>
    protected abstract Task<QueryResult> QueryAsync(string accessToken, EmailQuery query, CancellationToken cancellationToken);

    /// <summary>Provider-specific folder/label listing. Throws on 401.</summary>
    protected abstract Task<IReadOnlyList<MailFolder>> FetchFoldersAsync(string accessToken, CancellationToken cancellationToken);

    public async Task<EmailFetchResult> FetchAsync(EmailConnection connection, CancellationToken cancellationToken = default)
    {
        var query = EmailQuery.From(connection);
        try
        {
            var (ok, value) = await WithAuthAsync(connection, (tok, c) => QueryAsync(tok, query, c), cancellationToken);
            return ok ? EmailFetchResult.Ok(value!.Messages, value.Saturated) : EmailFetchResult.Reconsent;
        }
        catch (MailTransientException ex)
        {
            logger.LogWarning(ex, "Transient error reading {Provider} connection {Id}; skipping this poll", Provider, connection.Id);
            return EmailFetchResult.Ok([]);
        }
        catch (HttpRequestException ex)
        {
            // A non-transient, non-401 HTTP error (e.g. a 400 "restriction too complex") must not crash the
            // cycle: flag the connection and don't advance the cursor, so no mail in the window is skipped.
            logger.LogWarning(ex, "Non-transient HTTP error reading {Provider} connection {Id}; flagging needs-reconsent", Provider, connection.Id);
            await MarkNeedsReconsentAsync(connection, cancellationToken);
            return EmailFetchResult.Reconsent;
        }
    }

    public async Task<EmailFoldersResult> ListFoldersAsync(EmailConnection connection, CancellationToken cancellationToken = default)
    {
        try
        {
            var (ok, value) = await WithAuthAsync(connection, (tok, c) => FetchFoldersAsync(tok, c), cancellationToken);
            return ok ? EmailFoldersResult.Ok(value!) : EmailFoldersResult.Reconsent;
        }
        catch (MailTransientException ex)
        {
            logger.LogWarning(ex, "Transient error listing folders for {Provider} connection {Id}", Provider, connection.Id);
            return EmailFoldersResult.Ok([]);
        }
    }

    /// <summary>Runs a provider call with the connection's token; on a 401 refreshes once and retries, flagging needs-reconsent if that still fails.</summary>
    private async Task<(bool Ok, T? Value)> WithAuthAsync<T>(EmailConnection connection, Func<string, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = tokens.Unprotect(connection.AccessToken);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // A token that no longer unprotects (key-ring reset, foreign purpose) is a dead connection, not a crash:
            // flag it so the user reconnects (ADR-V016).
            logger.LogWarning(ex, "Stored token for {Provider} connection {Id} cannot be unprotected; flagging needs-reconsent", Provider, connection.Id);
            await MarkNeedsReconsentAsync(connection, cancellationToken);
            return (false, default);
        }
        try
        {
            return (true, await call(accessToken, cancellationToken));
        }
        catch (MailUnauthorizedException)
        {
            var refreshed = await TryRefreshAsync(connection, cancellationToken);
            if (refreshed is null)
            {
                await MarkNeedsReconsentAsync(connection, cancellationToken);
                return (false, default);
            }
            try
            {
                return (true, await call(refreshed, cancellationToken));
            }
            catch (MailUnauthorizedException)
            {
                await MarkNeedsReconsentAsync(connection, cancellationToken);
                return (false, default);
            }
        }
    }

    private async Task<string?> TryRefreshAsync(EmailConnection c, CancellationToken cancellationToken)
    {
        try
        {
            var refreshed = await consent.RefreshAsync(c.Provider, tokens.Unprotect(c.RefreshToken), cancellationToken);
            c.AccessToken = tokens.Protect(refreshed.AccessToken);
            c.RefreshToken = tokens.Protect(refreshed.RefreshToken);
            c.TokenExpiresAt = refreshed.ExpiresAt;
            c.UpdatedAt = clock.GetUtcNow();
            connections.Update(c);
            await connections.SaveChangesAsync(cancellationToken);
            return refreshed.AccessToken;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token refresh failed for {Provider} connection {Id}", Provider, c.Id);
            return null;
        }
    }

    private async Task MarkNeedsReconsentAsync(EmailConnection c, CancellationToken cancellationToken)
    {
        c.Status = EmailConnectionStatuses.NeedsReconsent;
        c.UpdatedAt = clock.GetUtcNow();
        connections.Update(c);
        await connections.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Maps a provider status to the typed exceptions the auth wrapper understands; surfaces the body of other failures.</summary>
    internal static async Task ThrowForStatusAsync(HttpResponseMessage resp, string provider, CancellationToken cancellationToken)
    {
        if (resp.IsSuccessStatusCode) return;
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new MailUnauthorizedException();
        if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
            throw new MailTransientException($"{provider} returned {(int)resp.StatusCode}.");

        var detail = "";
        try
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body)) detail = " — " + (body.Length > 500 ? body[..500] : body);
        }
        catch { /* diagnostics only */ }
        throw new HttpRequestException($"{provider} returned {(int)resp.StatusCode}.{detail}");
    }
}

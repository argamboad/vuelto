using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vuelto.Core.Entities;
using Vuelto.Core.Mail;
using Vuelto.Core.Repositories;
using Vuelto.Core.Vouchers;

namespace Vuelto.Infrastructure.Mail;

/// <summary>
/// Gmail reader (EMAIL-3): the list endpoint returns ids for the <c>q</c> match (paged via
/// <c>nextPageToken</c>, capped → <c>Saturated</c>); each message is then fetched and its HTML part
/// decoded; one un-fetchable message never aborts the batch. Read-only (GET only) against the fixed
/// Gmail host (R76 allowlist).
/// </summary>
public sealed class GmailEmailReader(HttpClient http, IEmailTokenProtector tokens, IMailConsentService consent, IRepository<EmailConnection> connections, TimeProvider clock, ILogger<GmailEmailReader> logger)
    : OAuthEmailReader(tokens, consent, connections, clock, logger)
{
    private const string GmailBase = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const int MaxPages = 20;

    public override string Provider => EmailProviders.Google;

    protected override async Task<QueryResult> QueryAsync(string accessToken, EmailQuery query, CancellationToken cancellationToken)
    {
        var q = GmailQueryBuilder.BuildQ(query);
        var ids = new List<string>();
        string? pageToken = null;
        var saturated = false;

        for (var page = 0; ; page++)
        {
            if (page >= MaxPages) { saturated = true; break; }
            var listUrl = $"{GmailBase}/messages?q={Uri.EscapeDataString(q)}&maxResults={query.MaxResults}" + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var doc = await GetJsonAsync(listUrl, accessToken, cancellationToken);
            if (doc.RootElement.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.GetString() is { } s) ids.Add(s);
            var next = doc.RootElement.TryGetProperty("nextPageToken", out var tok) ? tok.GetString() : null;
            if (string.IsNullOrEmpty(next)) break;
            pageToken = next;
        }

        var results = new List<VoucherMessage>();
        foreach (var id in ids)
        {
            try
            {
                var message = await GetMessageAsync(id, accessToken, cancellationToken);
                if (message is not null) results.Add(message);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogWarning(ex, "Gmail message {Id} fetch failed; skipping it this poll", id);
            }
        }
        return new QueryResult(results, saturated);
    }

    protected override async Task<IReadOnlyList<MailFolder>> FetchFoldersAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"{GmailBase}/labels", accessToken, cancellationToken);
        var folders = new List<MailFolder>();
        if (doc.RootElement.TryGetProperty("labels", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var l in arr.EnumerateArray())
            {
                var id = l.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var name = l.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (id is not null && name is not null) folders.Add(new MailFolder(id, name));
            }
        return folders;
    }

    private async Task<VoucherMessage?> GetMessageAsync(string id, string accessToken, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"{GmailBase}/messages/{Uri.EscapeDataString(id)}?format=full", accessToken, cancellationToken);
        var root = doc.RootElement;
        if (!root.TryGetProperty("payload", out var payload)) return null;

        string? subject = null, sender = null;
        if (payload.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
            foreach (var h in headers.EnumerateArray())
            {
                var name = h.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = h.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (string.Equals(name, "Subject", StringComparison.OrdinalIgnoreCase)) subject = value;
                else if (string.Equals(name, "From", StringComparison.OrdinalIgnoreCase)) sender = value;
            }

        DateTimeOffset? received = root.TryGetProperty("internalDate", out var idt) && long.TryParse(idt.GetString(), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        return new VoucherMessage(id, subject, sender, received, ExtractHtml(payload));
    }

    /// <summary>Walks the MIME tree for the first text/html part and base64url-decodes it.</summary>
    private static string ExtractHtml(JsonElement payload)
    {
        var mime = payload.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
        if (mime == "text/html" && payload.TryGetProperty("body", out var body) && body.TryGetProperty("data", out var data) && data.GetString() is { } encoded)
            return DecodeBase64Url(encoded);
        if (payload.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            foreach (var part in parts.EnumerateArray())
            {
                var html = ExtractHtml(part);
                if (!string.IsNullOrEmpty(html)) return html;
            }
        return "";
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, cancellationToken);
        await ThrowForStatusAsync(resp, "Gmail", cancellationToken);
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string DecodeBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return ""; }
    }
}

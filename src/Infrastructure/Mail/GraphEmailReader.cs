using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vuelto.Core.Entities;
using Vuelto.Core.Mail;
using Vuelto.Core.Repositories;
using Vuelto.Core.Vouchers;

namespace Vuelto.Infrastructure.Mail;

/// <summary>
/// Microsoft Graph reader (EMAIL-3): fetches per folder with the full filter in <c>$filter</c>, pages
/// <c>@odata.nextLink</c> (capped → <c>Saturated</c>), isolates a failing folder, recurses child
/// folders for the picker with full-path names. Read-only (GET only). Destinations are the fixed Graph
/// host plus the nextLink Graph itself returned — verified to stay on that host (R76 allowlist).
/// </summary>
public sealed class GraphEmailReader(HttpClient http, IEmailTokenProtector tokens, IMailConsentService consent, IRepository<EmailConnection> connections, TimeProvider clock, ILogger<GraphEmailReader> logger)
    : OAuthEmailReader(tokens, consent, connections, clock, logger)
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const int MaxPages = 20;
    private const int MaxFolderDepth = 5;

    public override string Provider => EmailProviders.Microsoft;

    protected override async Task<QueryResult> QueryAsync(string accessToken, EmailQuery query, CancellationToken cancellationToken)
    {
        var folders = query.Folders.Count > 0 ? query.Folders : ["inbox"];
        var results = new List<VoucherMessage>();
        var saturated = false;

        foreach (var folder in folders)
        {
            try
            {
                if (await FetchFolderAsync(folder, query, accessToken, results, cancellationToken)) saturated = true;
            }
            catch (HttpRequestException ex)
            {
                // A bad folder (e.g. a 400 on one mailbox) must not abort the others; 401 and 429/5xx are typed and still bubble.
                Logger.LogWarning(ex, "Graph folder '{Folder}' fetch failed; skipping it this poll", folder);
            }
        }

        return new QueryResult(results.OrderBy(m => m.ReceivedAt ?? DateTimeOffset.MinValue).ToList(), saturated);
    }

    /// <summary>Pages one folder into <paramref name="acc"/>; true if the page cap was hit.</summary>
    private async Task<bool> FetchFolderAsync(string folder, EmailQuery query, string accessToken, List<VoucherMessage> acc, CancellationToken cancellationToken)
    {
        var url = GraphBase + GraphQueryBuilder.MessagesUrl(folder, query);
        for (var page = 0; page < MaxPages; page++)
        {
            using var doc = await GetJsonAsync(url, accessToken, cancellationToken);
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray()) acc.Add(Map(m));

            var next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
            if (string.IsNullOrEmpty(next)) return false;
            if (!Uri.TryCreate(next, UriKind.Absolute, out var nextUri) || !string.Equals(nextUri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning("Graph nextLink pointed off-host ({Host}); stopping this folder", nextUri?.Host);
                return true;
            }
            url = next;
        }
        return true;
    }

    protected override async Task<IReadOnlyList<MailFolder>> FetchFoldersAsync(string accessToken, CancellationToken cancellationToken)
    {
        var folders = new List<MailFolder>();
        await CollectFoldersAsync("/me/mailFolders", parentPath: null, depth: 0, accessToken, folders, cancellationToken);
        return folders;
    }

    private async Task CollectFoldersAsync(string relativeUrl, string? parentPath, int depth, string accessToken, List<MailFolder> acc, CancellationToken cancellationToken)
    {
        if (depth > MaxFolderDepth) return;
        using var doc = await GetJsonAsync(GraphBase + relativeUrl + "?$select=id,displayName,childFolderCount&$top=100", accessToken, cancellationToken);
        if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        var level = new List<(string Id, string Name, int Children)>();
        foreach (var f in arr.EnumerateArray())
        {
            var id = f.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var name = f.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            if (id is null || name is null) continue;
            level.Add((id, name, f.TryGetProperty("childFolderCount", out var cc) && cc.TryGetInt32(out var n) ? n : 0));
        }

        foreach (var (id, name, children) in level)
        {
            var path = parentPath is null ? name : $"{parentPath}/{name}";
            acc.Add(new MailFolder(id, path));
            if (children > 0)
                await CollectFoldersAsync($"/me/mailFolders/{Uri.EscapeDataString(id)}/childFolders", path, depth + 1, accessToken, acc, cancellationToken);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, cancellationToken);
        await ThrowForStatusAsync(resp, "Graph", cancellationToken);
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static VoucherMessage Map(JsonElement m)
    {
        var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var subject = m.TryGetProperty("subject", out var s) ? s.GetString() : null;
        string? sender = null;
        if (m.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object
            && from.TryGetProperty("emailAddress", out var ea) && ea.TryGetProperty("address", out var addr))
            sender = addr.GetString();
        DateTimeOffset? received = m.TryGetProperty("receivedDateTime", out var rd) && rd.TryGetDateTimeOffset(out var dto) ? dto : null;
        var html = m.TryGetProperty("body", out var body) && body.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
        return new VoucherMessage(id, subject, sender, received, html);
    }
}

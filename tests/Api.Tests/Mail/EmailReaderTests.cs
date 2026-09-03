using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Core.Mail;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;
using Vuelto.Infrastructure.Mail;

namespace Vuelto.Api.Tests.Mail;

/// <summary>
/// EMAIL-3 (donor US-027 + WU-3 A5/A9) on real Postgres for the persisted side effects: Graph and Gmail
/// readers push every filter into the provider query, map payloads, refresh once on 401 (persisting the new
/// tokens), flag needs-reconsent when the refresh fails, skip quietly on 429, page through nextLink /
/// nextPageToken, isolate a failing folder, list folders recursively with path names, and never issue a
/// non-GET request.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmailReaderTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeEmailTokenProtector _tokens = new();

    private const string OneGraphMessage = """
        {"value":[{"id":"AAMk-1","subject":"Notificación de transacción TACO BELL",
          "from":{"emailAddress":{"address":"notificacion@notificacionesbaccr.com"}},
          "receivedDateTime":"2026-06-16T15:15:00Z","body":{"contentType":"html","content":"<html><body>voucher</body></html>"}}]}
        """;

    private sealed class FakeConsent : IMailConsentService
    {
        public Func<string, string, MailConsentTokens>? OnRefresh;
        public int RefreshCalls;
        public string BuildAuthorizationUrl(string provider, string redirectUri, string state, string? loginHint = null) => "";
        public string ProtectState(Guid userId, string provider) => "";
        public bool TryReadState(string? state, out Guid userId, out string provider) { userId = Guid.Empty; provider = ""; return false; }
        public Task<MailConsentTokens> ExchangeCodeAsync(string provider, string code, string redirectUri, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailConsentTokens> RefreshAsync(string provider, string refreshToken, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return OnRefresh is null ? throw new HttpRequestException("invalid_grant") : Task.FromResult(OnRefresh(provider, refreshToken));
        }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        public List<(string Method, string Url)> Requests { get; } = [];
        public string DefaultBody { get; init; } = "{\"value\":[]}";
        public void Enqueue(HttpStatusCode status, string body) => _responses.Enqueue((status, body));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.ToString()));
            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, DefaultBody);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private async Task<(AppDbContext Db, EmailConnection Conn, Guid UserId)> SeedAsync(string provider, string[] folders)
    {
        var db = Fixture.CreateContext(Guid.CreateVersion7());
        var user = new User { Email = $"{Guid.NewGuid():N}@test.local", CreatedAt = T0, UpdatedAt = T0 };
        var conn = new EmailConnection
        {
            UserId = user.Id, Provider = provider, AccessToken = _tokens.Protect("old-access"), RefreshToken = _tokens.Protect("old-refresh"),
            Folders = folders, SenderFilters = ["notificacion@notificacionesbaccr.com"], SubjectFilters = ["Notificación de transacción"],
            ImportFrom = T0.AddDays(-1), LastPolledAt = T0.AddDays(-1), CreatedAt = T0, UpdatedAt = T0,
        };
        db.Add(user); db.Add(conn);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (db, await db.EmailConnections.SingleAsync(c => c.Id == conn.Id), user.Id);
    }

    private GraphEmailReader Graph(AppDbContext db, QueueHandler handler, FakeConsent consent) =>
        new(new HttpClient(handler), _tokens, consent, new EfRepository<EmailConnection>(db), new FakeTimeProvider(T0), NullLogger<GraphEmailReader>.Instance);

    // ---- Graph ----

    [Fact]
    public async Task Graph_Fetch_MapsThePayload_AndPushesTheFilterIntoTheQuery_GetOnly()
    {
        var (db, conn, _) = await SeedAsync(EmailProviders.Microsoft, ["Inbox"]);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.OK, OneGraphMessage);

        var result = await Graph(db, handler, new FakeConsent()).FetchAsync(conn);

        Assert.False(result.NeedsReconsent);
        var m = Assert.Single(result.Messages);
        Assert.Equal(("AAMk-1", "notificacion@notificacionesbaccr.com"), (m.MessageId, m.Sender));
        Assert.Contains("voucher", m.HtmlBody);
        var url = Uri.UnescapeDataString(handler.Requests[0].Url);
        Assert.Contains("/me/mailFolders/Inbox/messages", url);
        Assert.Contains("isRead eq false", url);
        Assert.Contains("startswith(subject", url);
        Assert.All(handler.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task Graph_Fetch_RefreshesOnce_On401_PersistsTheNewTokens_AndRetries()
    {
        var (db, conn, _) = await SeedAsync(EmailProviders.Microsoft, ["Inbox"]);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, "");
        handler.Enqueue(HttpStatusCode.OK, OneGraphMessage);
        var consent = new FakeConsent { OnRefresh = (p, rt) => new MailConsentTokens("new-access", "new-refresh", T0.AddHours(1), null) };

        var result = await Graph(db, handler, consent).FetchAsync(conn);

        Assert.Single(result.Messages);
        Assert.Equal(1, consent.RefreshCalls);
        db.ChangeTracker.Clear();
        var reloaded = await db.EmailConnections.SingleAsync(c => c.Id == conn.Id);
        Assert.Equal(("new-access", "new-refresh", EmailConnectionStatuses.Active), (_tokens.Unprotect(reloaded.AccessToken), _tokens.Unprotect(reloaded.RefreshToken), reloaded.Status));
    }

    [Fact]
    public async Task Graph_Fetch_FlagsNeedsReconsent_WhenTheRefreshFails()
    {
        var (db, conn, _) = await SeedAsync(EmailProviders.Microsoft, ["Inbox"]);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, "");

        var result = await Graph(db, handler, new FakeConsent()).FetchAsync(conn);

        Assert.True(result.NeedsReconsent);
        Assert.Empty(result.Messages);
        db.ChangeTracker.Clear();
        Assert.Equal(EmailConnectionStatuses.NeedsReconsent, (await db.EmailConnections.SingleAsync(c => c.Id == conn.Id)).Status);
    }

    [Fact]
    public async Task Graph_Fetch_SkipsQuietly_OnTransient429()
    {
        var (db, conn, _) = await SeedAsync(EmailProviders.Microsoft, ["Inbox"]);
        var handler = new QueueHandler();
        handler.Enqueue((HttpStatusCode)429, "");
        var result = await Graph(db, handler, new FakeConsent()).FetchAsync(conn);
        Assert.False(result.NeedsReconsent);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public async Task Graph_Fetch_SkipsAFailingFolder_AndStillReadsTheOthers()
    {
        var (db, conn, _) = await SeedAsync(EmailProviders.Microsoft, ["BadFolder", "Inbox"]);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"restriction too complex"}}""");
        handler.Enqueue(HttpStatusCode.OK, OneGraphMessage);
        var result = await Graph(db, handler, new FakeConsent()).FetchAsync(conn);
        Assert.False(result.NeedsReconsent);
        Assert.Equal("AAMk-1", Assert.Single(result.Messages).MessageId);
    }

    [Fact]
    public async Task Graph_Fetch_FollowsNextLink_OnGraphHostOnly_AndSortsOldestFirst()
    {
        var (db, conn, _) = await SeedAsync(EmailProviders.Microsoft, ["Inbox"]);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"value":[{"id":"p1","subject":"s","from":{"emailAddress":{"address":"a@b.com"}},"receivedDateTime":"2026-06-16T11:00:00Z","body":{"content":"x"}}],
             "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/mailFolders/Inbox/messages?$skiptoken=abc"}
            """);
        handler.Enqueue(HttpStatusCode.OK, """
            {"value":[{"id":"p2","subject":"s","from":{"emailAddress":{"address":"a@b.com"}},"receivedDateTime":"2026-06-16T10:00:00Z","body":{"content":"x"}}],
             "@odata.nextLink":"https://evil.example/steal?token"}
            """);
        var result = await Graph(db, handler, new FakeConsent()).FetchAsync(conn);
        Assert.Equal(["p2", "p1"], result.Messages.Select(m => m.MessageId)); // oldest first
        Assert.True(result.Saturated); // the off-host link stopped paging early → treated as "more may exist"
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Graph_ListFolders_RecursesChildFolders_WithPathNames_AndRefreshesOn401()
    {
        var (db, conn, _) = await SeedAsync(EmailProviders.Microsoft, []);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, "");
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"id-inbox","displayName":"Inbox","childFolderCount":1}]}""");
        handler.Enqueue(HttpStatusCode.OK, """{"value":[{"id":"id-vouchers","displayName":"Vouchers","childFolderCount":0}]}""");
        var consent = new FakeConsent { OnRefresh = (p, rt) => new MailConsentTokens("new-access", "new-refresh", T0.AddHours(1), null) };

        var result = await Graph(db, handler, consent).ListFoldersAsync(conn);

        Assert.False(result.NeedsReconsent);
        Assert.Contains(result.Folders, f => f.Id == "id-inbox" && f.Name == "Inbox");
        Assert.Contains(result.Folders, f => f.Id == "id-vouchers" && f.Name == "Inbox/Vouchers");
        Assert.Contains(handler.Requests, r => r.Url.Contains("/me/mailFolders/id-inbox/childFolders"));
    }

    // ---- Gmail ----

    [Fact]
    public async Task Gmail_Fetch_ListsByQ_ThenGetsAndDecodesHtml_PagingByToken_GetOnly()
    {
        var (db, conn, _) = await SeedAsync(EmailProviders.Google, ["INBOX"]);
        static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var message = """
            {"id":"g-1","internalDate":"1750000000000","payload":{"headers":[{"name":"Subject","value":"Voucher Digital"},{"name":"From","value":"BN <bncontacto@bncr.fi.cr>"}],
              "mimeType":"multipart/alternative","parts":[{"mimeType":"text/plain","body":{"data":"__PLAIN__"}},{"mimeType":"text/html","body":{"data":"__HTML__"}}]}}
            """.Replace("__PLAIN__", B64Url("plain")).Replace("__HTML__", B64Url("<html><body>BN voucher CRC 8,390.00</body></html>"));
        var handler = new QueueHandler { DefaultBody = message };
        handler.Enqueue(HttpStatusCode.OK, """{"messages":[{"id":"g-1"}],"nextPageToken":"tok2"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"messages":[{"id":"g-2"}]}""");
        var reader = new GmailEmailReader(new HttpClient(handler), _tokens, new FakeConsent(), new EfRepository<EmailConnection>(db), new FakeTimeProvider(T0), NullLogger<GmailEmailReader>.Instance);

        var result = await reader.FetchAsync(conn);

        Assert.Equal(2, result.Messages.Count);
        var m = result.Messages[0];
        Assert.Equal(("g-1", "Voucher Digital"), (m.MessageId, m.Subject));
        Assert.Contains("bncontacto@bncr.fi.cr", m.Sender);
        Assert.Contains("BN voucher CRC 8,390.00", m.HtmlBody);
        Assert.Contains("is%3Aunread", handler.Requests[0].Url);
        Assert.Contains("pageToken=tok2", handler.Requests[1].Url);
        Assert.All(handler.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task Gmail_ListFolders_MapsLabels()
    {
        var (db, conn, _) = await SeedAsync(EmailProviders.Google, []);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"labels":[{"id":"INBOX","name":"INBOX"},{"id":"Label_7","name":"Bancos/BN"}]}""");
        var reader = new GmailEmailReader(new HttpClient(handler), _tokens, new FakeConsent(), new EfRepository<EmailConnection>(db), new FakeTimeProvider(T0), NullLogger<GmailEmailReader>.Instance);
        var result = await reader.ListFoldersAsync(conn);
        Assert.Equal(2, result.Folders.Count);
        Assert.Contains(result.Folders, f => f.Id == "Label_7" && f.Name == "Bancos/BN");
    }
}

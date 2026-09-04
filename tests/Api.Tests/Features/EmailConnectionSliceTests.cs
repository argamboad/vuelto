using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Email;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Api.Tests.Mail;
using Vuelto.Core.Entities;
using Vuelto.Core.Mail;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// EMAIL-2 on real Postgres (donor US-026 + US-035 AC1): create-time defaults and token protection,
/// validation codes, one inbox per provider per user (both providers allowed), user scoping on
/// get/update/delete, the backfill cursor rule (lowering import_from pulls the cursor back; raising it
/// never advances it), and the account-erasure contributor.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmailConnectionSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ctx(AppDbContext Db, EmailConnectionHandler Handler, FakeEmailTokenProtector Tokens, Guid UserId, Guid OtherUserId);

    private async Task<Ctx> ContextAsync()
    {
        var db = Fixture.CreateContext(Guid.CreateVersion7());
        var user = new User { Email = $"{Guid.NewGuid():N}@test.local", CreatedAt = T0, UpdatedAt = T0 };
        var other = new User { Email = $"{Guid.NewGuid():N}@test.local", CreatedAt = T0, UpdatedAt = T0 };
        db.AddRange(user, other);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var tokens = new FakeEmailTokenProtector();
        return new Ctx(db, new EmailConnectionHandler(new EfRepository<EmailConnection>(db), tokens, new FakeTimeProvider(T0)), tokens, user.Id, other.Id);
    }

    private static NewEmailConnection Valid(string provider = EmailProviders.Microsoft, string[]? senders = null, string[]? subjects = null, string access = "access-123", string refresh = "refresh-456") =>
        new(provider, "user@example.com", access, refresh, T0.AddHours(1), senders ?? [], subjects ?? ["Notificación de transacción"]);

    private sealed class FakeFolderReader(EmailFoldersResult result) : IEmailReader
    {
        public string Provider => EmailProviders.Microsoft;
        public int Calls;
        public Task<EmailFetchResult> FetchAsync(EmailConnection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EmailFoldersResult> ListFoldersAsync(EmailConnection connection, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(result); }
    }

    [Fact]
    public async Task BackfillFolderNames_ResolvesLegacyIdsOnce_AndLeavesDeadTokensAlone()
    {
        var c = await ContextAsync();
        var created = (await c.Handler.CreateAsync(c.UserId, Valid(), default)).Connection!;
        created.Folders = ["id-inbox", "id-vouchers", "id-gone"]; // a row from before names were stored
        created.FolderNames = [];
        c.Db.Update(created); await c.Db.SaveChangesAsync(); c.Db.ChangeTracker.Clear();

        // Dead token: nothing changes, nothing is written; the client gets nulls, never ids.
        var dead = new FakeFolderReader(EmailFoldersResult.Reconsent);
        var row = await c.Handler.GetAsync(c.UserId, created.Id, default);
        Assert.False(await c.Handler.BackfillFolderNamesAsync(row!, [dead], default));
        Assert.Empty(row!.FolderNames);
        Assert.All(ConnectionFolder.From(row), f => Assert.Null(f.Name));

        // Live provider: known ids get their names, an id the provider no longer lists stays unnamed.
        var live = new FakeFolderReader(EmailFoldersResult.Ok([new("id-inbox", "Inbox"), new("id-vouchers", "Inbox/Vouchers")]));
        Assert.True(await c.Handler.BackfillFolderNamesAsync(row, [live], default));
        c.Db.ChangeTracker.Clear();
        var stored = await c.Db.EmailConnections.SingleAsync(x => x.Id == created.Id);
        Assert.Equal(["Inbox", "Inbox/Vouchers", ""], stored.FolderNames);
        Assert.Equal([("id-inbox", "Inbox"), ("id-vouchers", "Inbox/Vouchers"), ("id-gone", (string?)null)], ConnectionFolder.From(stored).Select(f => (f.Id, f.Name)));

        // Once named, reads never touch the provider again (only the still-unnamed id would, on the next read).
        stored.Folders = ["id-inbox", "id-vouchers"]; stored.FolderNames = ["Inbox", "Inbox/Vouchers"];
        c.Db.Update(stored); await c.Db.SaveChangesAsync(); c.Db.ChangeTracker.Clear();
        var named = await c.Handler.GetAsync(c.UserId, created.Id, default);
        var untouched = new FakeFolderReader(EmailFoldersResult.Ok([]));
        Assert.False(await c.Handler.BackfillFolderNamesAsync(named!, [untouched], default));
        Assert.Equal(0, untouched.Calls);
    }

    private sealed class FakeStaging(Func<EmailConnection, StagingResult> stage) : IVoucherStagingService
    {
        public List<Guid> Staged { get; } = [];
        public Task<StagingResult> StageConnectionAsync(EmailConnection connection, CancellationToken cancellationToken = default)
        {
            Staged.Add(connection.Id);
            return Task.FromResult(stage(connection));
        }
    }

    [Fact]
    public async Task SyncAll_StagesEveryInboxOfTheCaller_SumsTheCounts_AndCountsDeadOnes()
    {
        var c = await ContextAsync();
        var outlook = (await c.Handler.CreateAsync(c.UserId, Valid(EmailProviders.Microsoft), default)).Connection!;
        var gmail = (await c.Handler.CreateAsync(c.UserId, Valid(EmailProviders.Google), default)).Connection!;
        var other = (await c.Handler.CreateAsync(c.OtherUserId, Valid(EmailProviders.Microsoft), default)).Connection!;

        var staging = new FakeStaging(conn => conn.Id == gmail.Id ? StagingResult.Reconsent : new StagingResult(2, 1, 3, false));
        var result = await c.Handler.SyncAllAsync(c.UserId, staging, default);

        Assert.Equal((1, 1, 2, 1, 3), (result.SyncedInboxes, result.NeedsReconsent, result.Staged, result.Duplicates, result.Unrecognized));
        Assert.Equal(new[] { gmail.Id, outlook.Id }.Order(), staging.Staged.Order()); // both of mine; never another user's
        Assert.DoesNotContain(other.Id, staging.Staged);

        var none = await c.Handler.SyncAllAsync(Guid.CreateVersion7(), staging, default);
        Assert.Equal((0, 0), (none.SyncedInboxes, none.NeedsReconsent));
    }

    private static UpdateEmailConnectionRequest Edit(string[]? subjects = null, int interval = 15, DateTimeOffset? importFrom = null, ConnectionFolder[]? folders = null, bool unread = true, bool ignoreCursor = false) =>
        new(folders, null, subjects ?? ["x"], unread, ignoreCursor, importFrom, interval);

    [Fact]
    public async Task Create_AppliesDefaults_ProtectsTokens_AndPersists()
    {
        var c = await ContextAsync();
        var (created, error) = await c.Handler.CreateAsync(c.UserId, Valid(), default);

        Assert.Null(error);
        Assert.Equal((true, T0, T0, 15, EmailConnectionStatuses.Active, c.UserId, "user@example.com"),
            (created!.UnreadOnly, created.ImportFrom, created.LastPolledAt, created.PollingIntervalMinutes, created.Status, created.UserId, created.AccountEmail));
        Assert.NotEqual("access-123", created.AccessToken);
        Assert.Equal(("access-123", "refresh-456"), (c.Tokens.Unprotect(created.AccessToken), c.Tokens.Unprotect(created.RefreshToken)));
        c.Db.ChangeTracker.Clear();
        var stored = await c.Db.EmailConnections.SingleAsync(x => x.Id == created.Id);
        Assert.Equal(["Notificación de transacción"], stored.SubjectFilters);
        Assert.Empty(stored.Folders);
    }

    [Theory]
    [InlineData("outlook", "invalid_provider")]
    [InlineData("", "invalid_provider")]
    public async Task Create_RejectsAnUnknownProvider(string provider, string code)
    {
        var c = await ContextAsync();
        var (created, error) = await c.Handler.CreateAsync(c.UserId, Valid(provider), default);
        Assert.Null(created);
        Assert.Equal(code, error!.Error);
    }

    [Fact]
    public async Task Create_RequiresTokens_AndAtLeastOneFilter()
    {
        var c = await ContextAsync();
        Assert.Equal("missing_tokens", (await c.Handler.CreateAsync(c.UserId, Valid(access: " "), default)).Error!.Error);
        Assert.Equal("filters_required", (await c.Handler.CreateAsync(c.UserId, Valid(subjects: [" "]), default)).Error!.Error);
        Assert.Empty(await c.Handler.ListAsync(c.UserId, default));
    }

    [Fact]
    public async Task Create_OneInboxPerProviderPerUser_BothProvidersAllowed_OtherUsersIndependent()
    {
        var c = await ContextAsync();
        Assert.Null((await c.Handler.CreateAsync(c.UserId, Valid(), default)).Error);
        Assert.Equal("connection_exists", (await c.Handler.CreateAsync(c.UserId, Valid(), default)).Error!.Error);
        Assert.Null((await c.Handler.CreateAsync(c.UserId, Valid(EmailProviders.Google), default)).Error);
        Assert.Null((await c.Handler.CreateAsync(c.OtherUserId, Valid(), default)).Error);
        Assert.Equal(["google", "microsoft"], (await c.Handler.ListAsync(c.UserId, default)).Select(x => x.Provider));
        Assert.Single(await c.Handler.ListAsync(c.OtherUserId, default));
    }

    [Fact]
    public async Task Update_EditsSettings_Validates_AndIsUserScoped()
    {
        var c = await ContextAsync();
        var created = (await c.Handler.CreateAsync(c.UserId, Valid(), default)).Connection!;

        var (updated, error) = await c.Handler.UpdateAsync(c.UserId, created.Id, Edit(subjects: ["Voucher Digital"], interval: 30,
            folders: [new("Inbox", "Inbox"), new(" id-vouchers ", " Inbox/Vouchers "), new("inbox", "dupe"), new("  ", "blank id")], unread: false, ignoreCursor: true), default);
        Assert.Null(error);
        Assert.Equal((false, true, 30), (updated!.UnreadOnly, updated.IgnoreCursor, updated.PollingIntervalMinutes));
        Assert.Equal(["Inbox", "id-vouchers"], updated.Folders); // trimmed, de-duplicated case-insensitively by id
        Assert.Equal(["Inbox", "Inbox/Vouchers"], updated.FolderNames); // names ride along, index-aligned
        Assert.Equal([("Inbox", "Inbox"), ("id-vouchers", "Inbox/Vouchers")], ConnectionFolder.From(updated).Select(f => (f.Id, f.Name)));

        // Ids without names keep the captured name; an id never named answers with itself.
        var (renamed, _) = await c.Handler.UpdateAsync(c.UserId, created.Id, Edit(subjects: ["Voucher Digital"], folders: [new("id-vouchers", null), new("Label_7", "")]), default);
        Assert.Equal(["Inbox/Vouchers", ""], renamed!.FolderNames);
        Assert.Equal([("id-vouchers", "Inbox/Vouchers"), ("Label_7", (string?)null)], ConnectionFolder.From(renamed).Select(f => (f.Id, f.Name)));
        Assert.Equal(["Voucher Digital"], updated.SubjectFilters);

        Assert.Equal("invalid_interval", (await c.Handler.UpdateAsync(c.UserId, created.Id, Edit(interval: 4), default)).Error!.Error);
        Assert.Equal("invalid_interval", (await c.Handler.UpdateAsync(c.UserId, created.Id, Edit(interval: 2000), default)).Error!.Error);
        Assert.Equal("filters_required", (await c.Handler.UpdateAsync(c.UserId, created.Id, Edit(subjects: []), default)).Error!.Error);
        Assert.Equal((null, null), await c.Handler.UpdateAsync(c.OtherUserId, created.Id, Edit(), default)); // not yours → not found, nothing changed
        Assert.Equal((null, null), await c.Handler.UpdateAsync(c.UserId, Guid.CreateVersion7(), Edit(), default));
    }

    [Fact]
    public async Task Update_LoweringImportFrom_PullsTheCursorBack_RaisingItNeverAdvancesIt()
    {
        var c = await ContextAsync();
        var created = (await c.Handler.CreateAsync(c.UserId, Valid(), default)).Connection!;
        Assert.Equal(T0, created.LastPolledAt);

        var older = T0.AddDays(-7);
        var lowered = (await c.Handler.UpdateAsync(c.UserId, created.Id, Edit(importFrom: older), default)).Connection!;
        Assert.Equal((older, older), (lowered.ImportFrom, lowered.LastPolledAt));

        var newer = T0.AddDays(-3);
        var raised = (await c.Handler.UpdateAsync(c.UserId, created.Id, Edit(importFrom: newer), default)).Connection!;
        Assert.Equal((newer, older), (raised.ImportFrom, raised.LastPolledAt)); // cursor stays — never skip un-imported mail
    }

    [Fact]
    public async Task Get_AndDelete_AreUserScoped()
    {
        var c = await ContextAsync();
        var created = (await c.Handler.CreateAsync(c.UserId, Valid(), default)).Connection!;
        Assert.Null(await c.Handler.GetAsync(c.OtherUserId, created.Id, default));
        Assert.False(await c.Handler.DeleteAsync(c.OtherUserId, created.Id, default));
        Assert.NotNull(await c.Handler.GetAsync(c.UserId, created.Id, default));
        Assert.True(await c.Handler.DeleteAsync(c.UserId, created.Id, default));
        Assert.Null(await c.Handler.GetAsync(c.UserId, created.Id, default));
    }

    [Fact]
    public async Task UserDataContributor_WipesOnlyThatUsersConnections()
    {
        var c = await ContextAsync();
        await c.Handler.CreateAsync(c.UserId, Valid(), default);
        await c.Handler.CreateAsync(c.UserId, Valid(EmailProviders.Google), default);
        await c.Handler.CreateAsync(c.OtherUserId, Valid(), default);

        await new EmailConnectionUserDataContributor(new EfRepository<EmailConnection>(c.Db)).WipeAsync(c.UserId);

        c.Db.ChangeTracker.Clear();
        Assert.Empty(await c.Handler.ListAsync(c.UserId, default));
        Assert.Single(await c.Handler.ListAsync(c.OtherUserId, default));
    }
}

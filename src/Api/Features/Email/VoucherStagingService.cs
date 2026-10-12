using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Mail;
using Vuelto.Core.Repositories;
using Vuelto.Core.Vouchers;

namespace Vuelto.Api.Features.Email;

/// <summary>
/// EMAIL-4: read → parse → stage. The connection is user-keyed, so the service resolves the owner's
/// <i>current</i> household and <b>enters that tenant</b> (ADR-003/V002) before touching household data —
/// the stamping interceptor and RLS then scope every draft. Per household: dedup by fingerprint (the
/// tombstone outlives the draft), bank resolved by name with a Cash fallback (seeding the default banks
/// first if the household has none, absorbing a concurrent seed), suggestions left blank until EMAIL-5.
/// Cursor rules: hold at the oldest transient failure so it retries (poison mail older than 7 days is
/// dropped, never stalls), resume from the newest fetched message when the reader saturated its page
/// cap, otherwise advance to the poll start. A failed save is detached so the shared context stays clean.
/// EMAIL-8: each draft carries the day's LIVE buy/sell pair, fetched once per sync and only when something is
/// staged — confirm books at it, however late the review. No live quote (provider down, only a stale cache)
/// stages the draft without one; it never blocks staging.
/// </summary>
public sealed class VoucherStagingService(
    IEnumerable<IEmailReader> readers,
    IVoucherParser parser,
    ITenantRepository tenants,
    ITenantContext tenantContext,
    IRepository<User> users,
    IRepository<Bank> banks,
    IBankDefaults bankDefaults, // the Catalog seeds banks (Arch A8)
    IRepository<PendingVoucher> pendingVouchers,
    IRepository<IngestedVoucher> ingestedVouchers,
    IRepository<EmailConnection> connections,
    IRepository<MerchantCategoryMapping> mappings,
    IExchangeRateService exchangeRates,
    TimeProvider clock,
    ILogger<VoucherStagingService> logger) : IVoucherStagingService
{
    public static readonly TimeSpan MaxRetryAge = TimeSpan.FromDays(7);

    public async Task<StagingResult> StageConnectionAsync(EmailConnection connection, CancellationToken cancellationToken = default)
    {
        var householdId = await tenants.GetTenantIdForUserAsync(connection.UserId, cancellationToken);
        if (householdId is null)
        {
            logger.LogWarning("Connection {Id} owner {User} has no household; skipping", connection.Id, connection.UserId);
            return StagingResult.Empty;
        }

        var reader = readers.FirstOrDefault(r => r.Provider == connection.Provider);
        if (reader is null)
        {
            logger.LogWarning("No reader for provider {Provider} (connection {Id})", connection.Provider, connection.Id);
            return StagingResult.Empty;
        }

        var pollStart = clock.GetUtcNow();
        var fetch = await reader.FetchAsync(connection, cancellationToken);
        if (fetch.NeedsReconsent) return StagingResult.Reconsent; // the reader flagged it; don't advance the cursor

        int staged = 0, duplicates = 0, unrecognized = 0;
        DateTimeOffset? oldestTransientFailedAt = null;
        DateTimeOffset? newestReceivedAt = null;

        using (tenantContext.EnterTenant(householdId.Value))
        {
            var locale = (await users.Query().Where(u => u.Id == connection.UserId).Select(u => u.Locale).FirstOrDefaultAsync(cancellationToken));
            var bankIds = await ResolveBankIdsAsync(householdId.Value, locale, cancellationToken);
            var rules = await mappings.Query().ToListAsync(cancellationToken); // EMAIL-5: the household's suggestion rules, matched in memory
            var quote = new Lazy<Task<ExchangeRateQuote?>>(() => LiveQuoteAsync(cancellationToken)); // EMAIL-8: asked once, and only if a draft is staged

            foreach (var message in fetch.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message.ReceivedAt is { } at && (newestReceivedAt is null || at > newestReceivedAt)) newestReceivedAt = at;
                try
                {
                    var parsed = parser.Parse(message);
                    if (parsed is null)
                    {
                        // Visible at the default level: a silent skip once hid a whole folder's missing mail.
                        logger.LogInformation("No voucher extractor for message {MessageId} from {Sender} with subject '{Subject}' (connection {Id}); skipped",
                            message.MessageId, message.Sender, message.Subject, connection.Id);
                        unrecognized++;
                        continue;
                    }

                    var fingerprint = VoucherFingerprint.Compute(parsed, message.MessageId);
                    if (fingerprint is not null && await ingestedVouchers.Query().AnyAsync(i => i.Fingerprint == fingerprint, cancellationToken))
                    {
                        duplicates++;
                        continue;
                    }
                    var effectiveFingerprint = fingerprint ?? Guid.CreateVersion7().ToString("N"); // no dedup possible: always stage

                    var now = clock.GetUtcNow();
                    var rate = await quote.Value;
                    var rule = MerchantMatcher.Resolve(rules, parsed.Merchant); // a suggestion is copied onto the draft, never applied (D4)
                    var draft = new PendingVoucher
                    {
                        TenantId = householdId.Value, EmailConnectionId = connection.Id, ProviderMessageId = message.MessageId, Fingerprint = effectiveFingerprint,
                        ParsedBank = parsed.Bank.ToString(), BankId = bankIds.For(parsed.Bank), Merchant = parsed.Merchant, Amount = parsed.Amount,
                        Currency = parsed.Currency, Date = parsed.Date, CardNumber = parsed.CardNumber, CardBrand = parsed.CardBrand, CardKind = parsed.CardKind, Authorization = parsed.Authorization,
                        Reference = parsed.Reference, TransactionType = parsed.TransactionType, MissingFields = parsed.MissingFields.ToArray(),
                        SuggestedCategoryId = rule?.CategoryId, SuggestedClass = rule is null ? null : rule.SuggestedClass ?? SuggestibleClasses.Default,
                        StagedRateBuy = rate?.Rates.Buy, StagedRateSell = rate?.Rates.Sell, StagedRateAsOf = rate?.AsOf,
                        Status = PendingVoucherStatuses.Pending, ReceivedAt = message.ReceivedAt, CreatedAt = now, UpdatedAt = now,
                    };
                    var tombstone = new IngestedVoucher { TenantId = householdId.Value, Fingerprint = effectiveFingerprint, PendingVoucherId = draft.Id, CreatedAt = now };

                    await pendingVouchers.AddAsync(draft, cancellationToken);
                    await ingestedVouchers.AddAsync(tombstone, cancellationToken);
                    try
                    {
                        await pendingVouchers.SaveChangesAsync(cancellationToken);
                    }
                    catch
                    {
                        // A failed save (e.g. a concurrent same-fingerprint unique violation) must not leave these
                        // Added rows tracked — removing an Added entity detaches it, so the next message is unaffected.
                        pendingVouchers.Remove(draft);
                        ingestedVouchers.Remove(tombstone);
                        throw;
                    }
                    staged++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var receivedAt = message.ReceivedAt;
                    if (receivedAt is { } r && pollStart - r > MaxRetryAge)
                        logger.LogWarning(ex, "Dropping poison message {MessageId} (older than {Days}d, connection {Id})", message.MessageId, (int)MaxRetryAge.TotalDays, connection.Id);
                    else
                    {
                        logger.LogWarning(ex, "Transient failure staging message {MessageId} (connection {Id}); cursor held for retry", message.MessageId, connection.Id);
                        if (receivedAt is { } t && (oldestTransientFailedAt is null || t < oldestTransientFailedAt)) oldestTransientFailedAt = t;
                    }
                }
            }
        }

        if (oldestTransientFailedAt is { } held && held < pollStart)
            connection.LastPolledAt = held;
        else if (fetch.Saturated && newestReceivedAt is { } newest)
        {
            var resume = newest - EmailQuery.CursorOverlap;
            connection.LastPolledAt = resume < pollStart ? resume : pollStart;
            logger.LogWarning("Connection {Id} hit the fetch page cap; resuming the cursor at {Resume:o}", connection.Id, connection.LastPolledAt);
        }
        else
            connection.LastPolledAt = pollStart;
        connection.UpdatedAt = clock.GetUtcNow();
        connections.Update(connection);
        await connections.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Connection {Id} synced: fetched {Fetched}, staged {Staged}, duplicates {Duplicates}, unrecognized {Unrecognized}; cursor now {Cursor:o}",
            connection.Id, fetch.Messages.Count, staged, duplicates, unrecognized, connection.LastPolledAt);
        return new StagingResult(staged, duplicates, unrecognized, false);
    }

    /// <summary>
    /// The provider's quote if it is LIVE — a stale cached pair can predate the purchase, so the draft goes without
    /// and confirm resolves today's (no worse than before EMAIL-8). Never throws: a missing rate never blocks staging.
    /// </summary>
    private async Task<ExchangeRateQuote?> LiveQuoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var quote = await exchangeRates.GetQuoteAsync(Currencies.Usd, Currencies.Crc, cancellationToken);
            if (quote.IsLive) return quote;
            logger.LogInformation("Only a cached exchange rate (as of {AsOf:o}) at staging; drafts staged without one", quote.AsOf);
        }
        catch (ExchangeRateUnavailableException ex)
        {
            logger.LogWarning(ex, "No exchange rate at staging; drafts staged without one, confirm resolves today's");
        }
        return null;
    }

    private sealed record BankIds(Guid? Bac, Guid? Bn, Guid? Cash)
    {
        public Guid? For(VoucherBank bank) => bank switch { VoucherBank.Bac => Bac ?? Cash, VoucherBank.BN => Bn ?? Cash, _ => Cash };
    }

    /// <summary>Map parsed banks to the household catalog by name (both locales), Cash as the fallback; seed the defaults first if the household has none.</summary>
    private async Task<BankIds> ResolveBankIdsAsync(Guid householdId, string? locale, CancellationToken cancellationToken)
    {
        await bankDefaults.EnsureSeededAsync(householdId, locale, cancellationToken); // a concurrent seed is absorbed there

        var all = await banks.Query().Select(b => new { b.Id, b.Name }).ToListAsync(cancellationToken);
        Guid? Find(params string[] names) => all.FirstOrDefault(b => names.Contains(b.Name, StringComparer.OrdinalIgnoreCase))?.Id;
        return new BankIds(Find("BAC Credomatic"), Find("Banco Nacional"), Find("Cash", "Efectivo"));
    }
}

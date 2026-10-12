# Epic `FX` — Exchange rate

> Registered epic key: **FX**. Port slice **P3** (ADR-V001): the live USD→CRC rate and its fallback
> chain, re-homed from donor story **US-014** (live rate as the source of truth) with the **US-034**
> guard (a non-positive provider rate is unavailable) — `vuelto-legacy/docs/stories/`. Decision
> context: ADR-V006 (live = truth for projections; frozen per transaction), ADR-V004 (every amount in
> both currencies), **ADR-V019 (the BCCR buy/sell pair and the direction rule)**, `FEATURES.md` §8,
> `DATA_MODEL.md` → "Rate resolution". No entity.

### FX-1 — Always know today's rate, never fabricate one

**As a** household member
**I want** the app to know what a dollar is worth in colones right now, and to tell me when it is
guessing from an older value
**So that** projections reflect today and every transaction I enter freezes a defensible rate

**Context / notes:** the default provider is the **Banco Central reference pair** (compra = `Buy`,
venta = `Sell`) read from the Finance Ministry mirror — no key, no quota (ADR-V019); the world feed
(exchangerate-api.com, one mid rate, keyed) stays selectable with `ExchangeRate__Provider=exchangerate-api`.
Both sit behind the Core seam `IExchangeRateService`, which answers an `FxRates` pair. A fetched rate counts as **live for one hour** (quota); the
cache never expires on its own, so when a refresh fails the stale value is served **flagged with its
fetch time**. `IExchangeRateResolver` runs the ADR-V006 chain: live → stale cache → **the household's
most recent transaction rate** (`IRecentRateSource`; P3 registers a source with nothing, P5 replaces
it with the transaction-backed one — the resolver never changes) → **unavailable**. Any member may
read. The world-feed provider with no `ExchangeRate:ApiKey` reports itself unavailable **without a
call** and the chain continues. Both provider hosts are fixed (the world feed also validates the two
currency codes as ISO before they enter its URL) — the clients are on the R76 outbound-URL allowlist
with that rationale, not behind the SSRF guard (nothing tenant-supplied reaches a URL).

**Which side applies (ADR-V019):** spending converts at the rate you would pay to fund it — a **USD**
purchase costs colones at **Sell**, a **CRC** purchase is worth dollars at **Buy**; income is the
mirror — **USD** income yields colones at **Buy**, **CRC** income buys dollars at **Sell**; budget
lines convert like spending. A transaction freezes the one side its currency selects. A single-rate
source (manual override, last-transaction tier, world feed) is the pair with both sides equal.

**Acceptance criteria**

```gherkin
Scenario: A live rate
  Given the BCCR mirror answers compra 448.27 / venta 453.69
  When I GET /api/exchange-rate
  Then I receive 200 { rate: 453.69, buy: 448.27, sell: 453.69, source: "live", as_of: <now> }
  And a second read within the hour makes no provider call and is still "live" with the same as_of

Scenario: The side follows the money
  Given today's pair is compra 448.27 / venta 453.69
  When a $20 voucher is confirmed
  Then it freezes 453.69 and reads ₡9,073.80
  When a ₡7,620 voucher is confirmed
  Then it freezes 448.27 and reads $17.00
  And the month's $3,000 income is worth ₡1,344,810 (compra), a $400 budget line ₡181,476 (venta)

Scenario: The provider is down but a rate was fetched earlier
  Given a rate was fetched three hours ago and the provider now fails
  When I GET /api/exchange-rate
  Then I receive 200 with source "cache" and as_of = the earlier fetch time

Scenario: The provider is down and nothing is cached, but the household has transactions
  Given the provider fails, nothing is cached, and my household's latest transaction froze 505.20
  When I GET /api/exchange-rate
  Then I receive 200 with source "transaction", buy = sell = 505.20, and as_of = that transaction's time

Scenario: Nothing anywhere
  Given the provider fails (or is not configured), nothing is cached, and my household has no transactions
  When I GET /api/exchange-rate
  Then I receive 503 { error: "exchange_rate_unavailable", message: "…try again later or enter one manually" }
  And no rate was invented

Scenario: Bad provider values never become rates
  Given the provider answers 0, a negative number, a failure result, or non-JSON
  Then the quote is unavailable and the failure is not cached — the next read tries again

Scenario: Anonymous
  When I GET /api/exchange-rate without a token
  Then I receive 401

Scenario: Home shows today's rate
  Given I am signed in
  When I open Home
  Then I see "buy ₡448.27 · sell ₡453.69 per $1" (or "₡505.20 per $1" from a single-rate source) with a
       green "live" badge — or a yellow "as of …" / "from your last transaction (…)" badge — or a red
       "Exchange rate unavailable" badge; never a bare number
```

**Out of scope (P3; landed in P5):** freezing the rate on a transaction and the 400 on create (the
transaction path calls the same resolver and freezes the side its currency selects); the
new-transaction form's pre-filled rate (the side for the chosen currency, a typed rate wins); a manual
rate override (one rate for both sides).

**Definition of done:** tests first; Api.Tests provider-client tests for both clients (fresh/stale
cache, failed refresh → stale not-live, failures not cached, non-positive/missing/malformed →
unavailable, no key / no URL → no call, USD→CRC only, fixed URL), resolver tests (the four tiers,
USD→CRC, the pair kept, the transaction tier as one rate), Core `FxRatesTests` (the direction table)
and the slice tests that freeze the side per currency (ledger create, voucher confirm) and convert
income/budget lines on the right side (dashboard), HTTP tests (401; 200-or-503 contract with buy/sell),
bUnit tests (badge: live / stale / transaction / unavailable / pair vs single; form prefill by currency;
dashboard rate line); R76 allowlist entries with rationale; `ExchangeRate` config section +
`.env.example` (`Provider`, `BccrUrl`); Postman folder; QA-FX-01..02 + regenerated PDFs; EN/ES resx;
merged, app working.

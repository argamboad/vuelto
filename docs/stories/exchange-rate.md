# Epic `FX` — Exchange rate

> Registered epic key: **FX**. Port slice **P3** (ADR-V001): the live USD→CRC rate and its fallback
> chain, re-homed from donor story **US-014** (live rate as the source of truth) with the **US-034**
> guard (a non-positive provider rate is unavailable) — `vuelto-legacy/docs/stories/`. Decision
> context: ADR-V006 (live = truth for projections; frozen per transaction), ADR-V004 (every amount in
> both currencies), `FEATURES.md` §8, `DATA_MODEL.md` → "Rate resolution". No entity.

### FX-1 — Always know today's rate, never fabricate one

**As a** household member
**I want** the app to know what a dollar is worth in colones right now, and to tell me when it is
guessing from an older value
**So that** projections reflect today and every transaction I enter freezes a defensible rate

**Context / notes:** the provider is exchangerate-api.com (free tier, 1,500 requests/month), behind
the Core seam `IExchangeRateService`. A fetched rate counts as **live for one hour** (quota); the
cache never expires on its own, so when a refresh fails the stale value is served **flagged with its
fetch time**. `IExchangeRateResolver` runs the ADR-V006 chain: live → stale cache → **the household's
most recent transaction rate** (`IRecentRateSource`; P3 registers a source with nothing, P5 replaces
it with the transaction-backed one — the resolver never changes) → **unavailable**. Any member may
read. With no `ExchangeRate:ApiKey` the provider reports itself unavailable **without a call** (the
local dev default) and the chain continues. The provider host is fixed and the two currency codes are
validated as ISO before they enter the URL — the client is on the R76 outbound-URL allowlist with that
rationale, not behind the SSRF guard (nothing tenant-supplied reaches the URL).

**Acceptance criteria**

```gherkin
Scenario: A live rate
  Given the provider answers 510.45 for USD→CRC
  When I GET /api/exchange-rate
  Then I receive 200 { rate: 510.45, source: "live", as_of: <now> }
  And a second read within the hour makes no provider call and is still "live" with the same as_of

Scenario: The provider is down but a rate was fetched earlier
  Given a rate was fetched three hours ago and the provider now fails
  When I GET /api/exchange-rate
  Then I receive 200 with source "cache" and as_of = the earlier fetch time

Scenario: The provider is down and nothing is cached, but the household has transactions
  Given the provider fails, nothing is cached, and my household's latest transaction froze 505.20
  When I GET /api/exchange-rate
  Then I receive 200 with source "transaction" and as_of = that transaction's time

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
  Then I see "₡510.45 per $1" with a green "live" badge — or a yellow "as of …" / "from your last
       transaction (…)" badge — or a red "Exchange rate unavailable" badge; never a bare number
```

**Out of scope:** freezing the rate on a transaction and the 400 on create (P5 — the transaction path
calls the same resolver); the new-transaction form's pre-filled rate (P5); a manual rate override.

**Definition of done:** tests first; Api.Tests provider-client tests (fresh/stale cache, failed refresh
→ stale not-live, failures not cached, non-positive/missing/malformed → unavailable, no key → no call,
ISO-code guard), resolver tests (the four tiers, USD→CRC), HTTP tests (401; 200-or-503 contract),
bUnit badge tests (live / stale / transaction / unavailable, mounted on Home); R76 allowlist entry
with rationale; `ExchangeRate` config section + `.env.example`; Postman folder; QA-FX-01..02 +
regenerated PDFs; EN/ES resx; merged, app working.

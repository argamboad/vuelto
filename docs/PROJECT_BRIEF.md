# Project Brief — ¿Y el vuelto?

> The lean PRD. Why this exists, what it is, what it is *not* (yet). The structure and the
> multi-tenant framing are constant; the content is the app.
>
> **Provenance.** This app is a **continuation port** of `vuelto-legacy/phase2` (the donor repo,
> frozen at its 2026-09-02 state) onto the Perezosoft platform. The donor's PRD v2.0, TDD v2.0,
> ADR-0001–0027 and 56 shipped stories are the source of truth for *what* the product does;
> this doc set is their translation into the platform's shape. See ADR-V001.

## Elevator pitch
A personal-finance app for **Costa Rican households that live in two currencies** (₡ CRC and
$ USD). It replaces a hand-maintained Excel budget: every expense is stored in both currencies at
the exchange rate of the day it happened, budget periods follow the household's **pay cycle**
(weekly, anchored on a chosen weekday) rather than the calendar, and card vouchers arriving by
email from Costa Rican banks are turned into transactions after a one-click review.

The name is Costa Rican slang — *"¿Y el vuelto?"*, "and the change?" — what a parent asks when
you come back from the corner store.

## The core loop
1. Money is spent — a card voucher email lands in the household's inbox, or someone types an
   expense in.
2. The transaction is captured **in both currencies at that day's rate** and lands in the right
   **budget month** (the anchor window containing its date, which may not be its calendar month).
3. The dashboard shows, for that month, **budgeted vs actual** per fixed/variable line, per week,
   per bank and payment method, plus income, unplanned essentials, expected refunds and savings
   envelopes — and the remainder.
4. The household adjusts: confirms or discards pending vouchers, edits the catalog, marks a refund
   received, reviews a category report, exports CSV.

Everything in the product serves step 2 (capture faithfully) and step 3 (see the month truthfully).

## Who it's for
- **Primary user:** a Costa Rican household (one or two earners, possibly paid in different
  currencies) that budgets weekly and receives bank voucher emails from BAC, BN and similar banks.
  Initially the owner's own household; multi-tenant from day one so any household can sign up.
- **Unit of account:** a **tenant** — labelled **Household** everywhere the user sees it. Data
  belongs to the household and is shared by its members; each member signs in as themselves,
  keeps their own language/theme, and connects their **own** inbox.

## What makes it different
- **Dual-currency by construction, not by conversion on read.** Every amount is stored as
  `original + currency + amount_crc + amount_usd + exchange_rate_used`; history never drifts when
  the rate moves. Projections use the live rate; actuals use the frozen one.
- **Pay-cycle months.** A month is 4 or 5 weeks starting on the household's weekday anchor
  (default: the last Thursday of the previous calendar month). Two income streams, each with 4-week
  and 5-week defaults, are snapshotted onto each auto-created month.
- **Email vouchers become transactions without ever marking mail read.** Read-only mailbox scopes,
  a per-household dedup fingerprint, and a review queue that the user confirms — a misparse is a
  fixable blank, never corrupt data.
- **Five transaction classes** that match how a household actually thinks: budgeted,
  extraordinary (discretionary), unplanned essential (with an expected-refund %), inflow, and
  envelope contribution — with the dashboard slicing each correctly.
- **Runs on the Perezosoft platform**, so multi-tenancy, auth, invitations, MFA, billing, jobs,
  notifications, GDPR and native shells are inherited, not built.

## MVP scope — what's IN
Everything the donor shipped (Slices 1–6 and 8 + hardening), re-homed as platform slices — see
`FEATURES.md` for the flows and `DECISIONS.md` ADR-V001 for the port plan:
- Multi-tenant foundation: tenants, users, tenant-scoped data, per-user preferences. *(constant)*
- **Budget settings** per household: week-start weekday, month anchor, two incomes × 4w/5w × currency.
- **Catalogs**: categories and banks (soft delete, reactivation offer, seeded once in the user's
  locale); savings **envelopes** with a reminder cadence.
- **Expense catalog**: fixed and variable budget lines (amount CRC/USD, payment method, optional
  bank, required category, ordering).
- **Months & weeks**: automatic lifecycle from transactions; editable per-month income.
- **Transactions**: manual entry/edit/delete in five classes; required bank + category; frozen
  exchange rate; derived **refunds** from unplanned essentials and their realization as inflows.
- **Live exchange rate** (exchangerate-api) with a stale-cache → last-transaction fallback chain.
- **Dashboard**: income, expense summary (card/account/total/remainder), budgeted-vs-actual lines,
  weekly breakdowns, unplanned slice, refunds, envelopes, bank × payment-method breakdown.
- **Reports**: category analysis (filterable); **CSV export** of transaction lists.
- **Email ingestion**: connect Outlook/Gmail (read-only), folder/sender/subject filters, background
  polling, BAC/BN voucher parsing, dedup, merchant→category suggestions, review queue → confirm.
- **Localization**: English (base) + Spanish; user data never translated.

## MVP scope — what's OUT (deferred / pinned)
- Non-web clients (mobile + desktop) — shells build from day one; feature parity **after** the web
  port reaches donor parity. *(constant)*
- **App signing, installers & store distribution — OUT for the platform, permanently (ADR-024).**
  Per-app work via `NEW_APP_GUIDE.md` Phase 9. *(constant)*
- **Data-driven bank definitions** (donor Slice 7, US-048–052) — DB-backed extractor definitions so
  a new bank is a data row, not a deploy. Resumes as the first post-parity epic. Trigger: ~bank #4–5.
- **LLM fallback for unknown voucher formats** — after data-driven definitions; never auto-inserts.
- **Excel category import** and **true auto-classification** — donor PRD §10 deviations 2 and 3.
- **Seed retranslation on language switch** — dropped in the port (ADR-V009); seeds are localized
  once at first use.
- **Per-month budget-line overrides**, bank sync / open banking, investment tracking, custom
  password auth, finer-grained per-resource permissions.
- **Billing plans for households** — the platform's Stripe machinery is present and must be
  configured, but no paid tier is defined yet (`PlanCatalog` placeholders stay).
- Provider push (Graph change notifications / Gmail Pub/Sub) instead of polling — revisit if host
  spin-down makes polling latency a problem.

## Guiding principles
- **Derived, not stored.** Dual-currency amounts, refund amounts, week counts, dashboard totals,
  envelope remainders and voucher fingerprints are computed from source data. The one deliberate
  exception: weeks are *materialized* at month creation so a later settings change never re-slices
  history (ADR-V005).
- **Tenant-scoped, not user-scoped.** Budget data belongs to the household. The deliberate
  carve-out: an **email connection is the user's** (their mailbox credential), and it survives the
  user leaving a household (ADR-V002). *(constant, with one documented exception)*
- **Clean API boundary.** UI is a client of the API; never hits the DB directly. *(constant)*
- **Lean MVP.** When in doubt, check the OUT list before building.
- **Freeze the rate, never recompute history.** A transaction's `exchange_rate_used` is set once.
- **Never touch the user's mail.** Read-only scopes; idempotency comes from fingerprints + cursors.
- **Nothing partial.** A rejected transaction never leaves an empty month; a failed voucher confirm
  never leaves an orphan; a refund flip creates exactly one inflow (conditional updates + savepoints).
- **Extend the platform, never modify it.** Anything the platform lacks that is *generic* goes
  upstream as a `perezosoft-platform` PR first; anything vuelto-specific is solved here by extension.

## Related docs
- `FEATURES.md` — concrete user flows and behavior.
- `DATA_MODEL.md` — entities, relationships, derived rules.
- `TECH_STACK.md` — stack & architecture (mostly constant).
- `DECISIONS.md` — ADR log (platform ADRs + the app's `ADR-V…` series).
- `../CLAUDE.md` — operating manual for Claude Code (repo root).
- Donor repo (`vuelto-legacy`, read-only reference): `docs/Vuelto_PRD_v2.0.md`, `docs/Vuelto_TDD_v2.0.md`,
  `docs/decisions.md`, `docs/stories/US-001…056.md`, `docs/qa/manual-qa-guide.md`.

# Decisions (ADR log)

> Lightweight architecture/product decision records: decision + rationale + date. Stops us (and
> Claude Code) from re-litigating settled choices. Append new ones; supersede with a new dated
> entry rather than rewriting.
>
> **vuelto's own decisions**, numbered **ADR-V001 …**. The platform's — the constant ADRs and its build decisions
> (`ADR-0NN`), ADR-034 the packages among them — are the platform's log:
> [perezosoft-platform `docs/DECISIONS.md`](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DECISIONS.md).
> Until the conversion (2026-11, Pkg V.4) this file carried a copy of them as of the clone; the packages made the copy
> stale, so it went. Where an `ADR-V` below cites a platform ADR, it is that log's.

## App decisions — ¿Y el vuelto? (`ADR-V…`)

> **Numbering.** ADR-001–024 above are the platform's own decisions and are inherited verbatim
> (treat them as constant here — they are re-decided upstream, never in this repo). This app's
> decisions use the **`ADR-V`** prefix so they can never collide with a future upstream ADR-025.
> Donor decisions are cited as `donor ADR-00NN` (`vuelto/docs/decisions.md`).
>
> **Donor decisions absorbed by the platform (not re-recorded):** donor ADR-0001/0002/0003
> (OAuth middleware, in-memory JWT + hashed rotating refresh cookie, verified-email account merge)
> → platform ADR-002; donor ADR-0004 (repository-layer tenant isolation) → platform ADR-003/020;
> donor ADR-0016's membership/invitation/departure mechanics + ADR-0023 (hash-at-rest invitation
> tokens, accept moves membership) → platform ADR-003/009/011 + `HouseholdInvitationsController`;
> donor ADR-0025 (I/O in Infrastructure, Api = composition) → platform ADR-004; donor ADR-0026
> (transactional email) → platform `IEmailSender` + outbox + `BrandedEmail`; donor ADR-0022
> (removed Flutter stack) — historical, moot.

**ADR-V001 — Entry mode: this app is a continuation port of `vuelto-legacy/phase2`; the donor is frozen, its tests are the spec, and the platform is extended, never modified. (2026-09-02)**
`vuelto` was cloned from `perezosoft-platform@d09c60f` (commit `0f7d1dc`) and carries the
**entire** platform (billing, jobs, notifications, files, GDPR, admin, MAUI shells) even where
vuelto has no immediate use for a subsystem. The donor repo `vuelto-legacy` (branch `develop`, 2026-09-02)
is **frozen as a read-only reference**; no product work continues there. The donor's shipped
behavior (Slices 1–6, 8 and the two audit-remediation tracks) is re-homed as vertical slices in the
order P0–P11 of the port plan; only then does the donor's unfinished roadmap resume here (data-driven
bank definitions first). **Rules:** (1) the donor's ~330 domain unit tests + 6 integration classes
port as the specification; its ~40 foundational-behavior tests (invitations, membership lifecycle,
concurrency) are run once against the platform as acceptance checks, then discarded; (2) the
platform is **extended through its seams** (`IRepository<T>`, `ITenantDataContributor`,
`IUserDataContributor`, `IScheduledJob`, `IEmailSender`, `ITenantContext.EnterTenant`, DI) — never
modified in this repo; a genuinely generic gap goes upstream as a `perezosoft-platform` PR first,
and anything vuelto-specific is solved here; (3) stories live one file per epic in `docs/stories/`,
each scenario citing the donor story it ports (`from US-015`), and new work continues the donor's
numbering from **US-057**.
*Rationale:* the donor is an unfinished project, not a finished product to migrate — the goal is to
stop paying twice for auth/tenancy/email/jobs and to inherit the platform's hardening. Freezing the
donor prevents divergence; porting behind its tests is the only honest parity proof.

**ADR-V002 — Household is the tenant; platform roles owner/admin/member apply; budget data is shared; an email connection is user-keyed. (2026-09-02; from donor ADR-0016, port decision D7)**
The platform `Tenant` is labelled **Household**. All budget data (settings, catalogs, expense lines,
months, weeks, transactions, refunds, envelopes, merchant mappings, pending/ingested vouchers) is
`ITenantScoped`. The platform's three roles apply unchanged: **any member** reads and edits budget
data (no new `Permission` — that is the member baseline); management capabilities (rename, invite,
remove, roles, transfer, dissolve, billing, export) follow `RolePermissions`. The donor's
owner-only matrix is a strict subset, so nothing is lost; `admin` is a gain. **`EmailConnection`
is user-keyed** (not `ITenantScoped`): it is a member's mailbox credential, it survives leave /
remove / dissolve, it is erased with the account (`IUserDataContributor`, R12), and the vouchers it
produces land in the household the member is in **at poll time** (the poll job resolves the
membership and `EnterTenant`s it). A member who switches households therefore re-routes their
inbox — documented, accepted.
*Rationale:* a household genuinely runs one budget from several identities; the credential is the
person's, the data is the household's. The platform's per-user carve-out (ADR-C2) exists for
exactly this shape.

**ADR-V003 — Budget structure settings (week start, month anchor, income defaults) are per household, in their own `BudgetSettings` row. (2026-09-02; port decision D2)**
The donor kept six budget columns on `User`; months and transactions were household-scoped, so a
two-member household could tile months by *whoever entered the transaction*. The port introduces a
tenant-scoped `BudgetSettings` entity (one row per household, created with the donor's defaults on
first use) and `TransactionService`/`MonthService` read it from the ambient tenant instead of
taking a `User`. `User` stays a pure platform entity (locale, theme only).
*Rationale:* budget structure is not a preference (ADR-C2); it fixes a latent donor bug and removes
the largest foundational↔domain coupling in the donor code.
*Superseded in part (2026-09-16):* the income defaults left `BudgetSettings` for household income lines — ADR-V023.
The row keeps week start and month anchor.

**ADR-V004 — Domain money is dual-currency fixed-point decimal; Stripe remains the source of truth for billing money only. (2026-09-02)**
The platform models no money (its `Subscription` is a projection). This app does: amounts are
`decimal` mapped to `NUMERIC(12,2)`; the per-transaction rate `NUMERIC(10,4)`; refund percentage
`NUMERIC(5,2)`; rounding is half-away-from-zero to 2 dp in `CurrencyMath.Round2`; every stored
amount carries its currency (`CRC` | `USD`) and its CRC/USD pair. Billing (plans, seats, Stripe)
is untouched by this — the two never mix.
*Rationale:* the platform's "no decimals" is a billing statement, not a domain rule; a finance app
needs precise, currency-tagged money. Recorded so the platform's ADR-006 is not misread as a ban.

**ADR-V005 — Pay-cycle months: anchor-window resolution, weeks materialized at creation, months auto-created from transactions and auto-deleted when empty, two incomes per month. (2026-09-02; from donor ADR-0006, 0007, 0012, 0013)**
A date belongs to the month whose anchor window contains it (may be a neighboring calendar month —
never resolve by calendar). `week_count` (4|5) and the weeks are computed once and **stored** so a
later settings change never re-slices history. Months exist only through transactions: created on
the first transaction in an uncovered window (income snapshotted from `BudgetSettings` by week
count), deleted with their weeks when the last transaction goes; there is **no manual month path**.
Month income is two incomes, each amount + currency, editable per month.
*Rationale:* budget periods follow pay cycles; auto-lifecycle removes an entire class of "empty
month" and "forgot to create the month" bugs; stored weeks give historical stability.
*Superseded in part (2026-09-16):* a month's income is no longer two slots snapshotted from 4w/5w defaults but a list of
rows snapshotted from the income lines by pay period — ADR-V023. The lifecycle rules above are unchanged.

*As built (P5a / LEDGER-1/2, 2026-09-03).* `MonthHandler.GetOrCreateForDateAsync` only <em>stages</em>
a new month and its weeks on the shared context; the transaction path validates everything, settles
the rate, then issues **one `SaveChanges`** that lands month + weeks + transaction atomically — no
`IUnitOfWork` scope is needed for create, update (which also stages the emptied source month's
removal) or delete. A lost concurrent-creation race (unique `TenantId, Year, MonthNumber`) surfaces
as a `DbUpdateException`; the handler detaches what it staged (`Remove` on Added → Detached) and
retries once, finding the winner's month. Settings come from the household's `BudgetSettings` row or
`BudgetSettings.Defaults` — the Ledger slice reads the entity through `IRepository<BudgetSettings>`
rather than the Budget slice's handler (R7: slices don't reference each other). The month read returns
weeks only; the dashboard summary arrives with P6.

**ADR-V006 — The live exchange rate is the source of truth for projections; each transaction freezes its rate forever. (2026-09-02; from donor ADR-0011)**
Months store no rate. Projections resolve: live quote (cached < 1 h counts as live — quota) →
stale cache flagged "as of …" → most recent transaction's rate → block (`exchange_rate_unavailable`).
`exchange_rate_used` is set at creation and never recalculated on edit. No transaction is created
without a rate; a provider rate ≤ 0 is unavailable. The provider (exchangerate-api, free tier) is
an `HttpClient` against a fixed host, allowlisted for the platform's outbound-URL guard (R76).
*Rationale:* actual spend must reflect the rate at purchase; projections must reflect today.

*As built (P3 / FX-1, 2026-09-03).* Three Core seams so any slice can ask for a rate without
referencing another slice (R7): `IExchangeRateService` (the provider — `ExchangeRateApiClient` in
`Infrastructure/ExchangeRate`, `IMemoryCache`, `ExchangeRate:{ApiKey,BaseUrl,FreshnessMinutes}`),
`IExchangeRateResolver` (the chain, in `Features/ExchangeRate`), and `IRecentRateSource` (the
last-transaction tier — P3 registers `NoRecentRateSource`; P5 swaps in the transaction-backed one, the
resolver untouched). An unset key makes the provider unavailable **without a call**, so a fresh
checkout degrades to the chain rather than erroring. `GET /api/exchange-rate` answers 200
`{rate, source, as_of}` or 503 `exchange_rate_unavailable`; Home carries an `ExchangeRateBadge`
(live / "as of …" / "from your last transaction" / unavailable). The ISO codes are validated before
they enter the URL, which is the allowlist rationale in `ArchitectureTests`. No pre-warming job: the
lazy one-hour cache already bounds quota, and a job would spend ~720 of the 1,500 monthly requests
warming a cache nobody may read.

**ADR-V007 — Five transaction classes; payment method and a required bank live on the transaction; category required; refunds are derived from unplanned essentials and realize as inflows; envelopes are transactional. (2026-09-02; from donor ADR-0009, 0010, 0014, 0018, 0019, 0020; *amended by ADR-V025: discretionary spend may carry a refund too*)**
Classes: `budgeted`, `extraordinary` (label "Discretionary"), `unplanned_essential` (label
"Unplanned"), `inflow` (money in, folded into income), `envelope_contribution` (requires an
envelope and `bank_account`; carved out of expenses/balance). The first three count as expenses.
Every transaction has a `payment_method` (`credit_card` default | `bank_account`) and a **required**
`bank_id` (Cash is a bank) and `category_id`. A `Refund` is derived from an `unplanned_essential`
transaction's refund-expected % (amounts = % × frozen amounts), re-synced on every edit, and only
its `status` is edited directly; `pending → received` creates a linked derived `inflow`
(`source = refund_realization`, read-only), symmetric on flip-back, guarded by a conditional update
so concurrent flips yield exactly one inflow. Envelopes have an annual target + reminder cadence
(`monthly` | `five_week_months`) and no static contribution.
*Rationale:* matches how the household models spend; a refund is a fraction of one transaction; a
realized refund is a real inflow; a bank-less transaction is meaningless for reconciliation.

**ADR-V008 — Catalogs are soft-deleted with the 409 reactivation offer; cross-tenant rows are uniformly invisible (404). (2026-09-02; from donor ADR-0008; port decision D6)**
Categories, banks and envelopes use `is_active`, never hard delete; names are unique per household
case-insensitively; a clash returns 409 `*_exists` (active) or `*_exists_inactive` + id (so the UI
offers Reactivate). Inactive names still render on historical rows. With the platform's global
filter + RLS, a row from another household **does not exist** from the caller's side: every
missing-or-foreign lookup is **404**. This retires the donor's 404-missing / 403-foreign split
(donor ADR-0004 as-built, US-062) — the platform bans the `…UnscopedAsync` reads that produced it (R5).
*Rationale:* deleting a catalog entry must never blank history; uniform 404 kills the existence
oracle the donor had already closed for opaque ids (US-037).

**ADR-V009 — Localization: localize chrome, never translate user data, seed once in the user's locale; English is the stored-value baseline. (2026-09-02; from donor ADR-0017 + 0021; port decision D4)**
UI chrome comes from the platform's `AppStrings` resx (EN base + ES, feature-prefixed keys,
parity-tested). User-entered names are never translated. Starter categories/banks are seeded
**once**, in the locale carried by the caller's JWT, and then are ordinary user data. The donor's
post-hoc `SeedRetranslationService` (rename untouched seeds on language switch) is **dropped** —
the platform owns the locale write and a slice cannot hook it without a cross-feature reference.
Stored values and column names are English (donor ADR-0021); the two deliberate value/label
mismatches (`extraordinary` → "Discretionary", `unplanned_essential` → "Unplanned") move from the
donor's `EnumLabels` into resx keys.
*Rationale:* translating user data corrupts intent; seed-then-freeze avoids silent rewrites of
history; retranslation was a nicety with a real coupling cost.

**ADR-V010 — Email ingestion: read-only mailbox scopes, user-keyed connections, staged review drafts with per-household dedup, confirm through the ordinary transaction path, polled by a platform scheduled job. (2026-09-02; from donor design D1–D6, ADR-0024, US-025–038, WU-3/WU-5)**
Consent uses the platform's Microsoft/Google OAuth client credentials with **read-only** mail
scopes (`Mail.Read` / `gmail.readonly`, `offline_access`, `openid email`) and an HMAC-signed state;
the pipeline **never marks mail read**. Tokens are encrypted with Data Protection (replacing the
donor's AES key). Readers (Graph, Gmail) push every filter into the provider query, **page** until
exhausted, refresh once on 401 then flip to `needs_reconsent`, and skip the poll on 429/5xx.
Parsing is data-routed (`BankVoucherMap`: `(sender, subject) → extractor`), fail-soft, and pure.
Vouchers stage as inert `PendingVoucher` drafts with a per-household `IngestedVoucher` tombstone
(SHA-256 of bank + auth|ref + amount + date, else message id; never silently dropped); tombstones
outlive confirm/discard. Suggestions are copied from merchant mappings (longest pattern wins),
never auto-applied. **Confirm is the only draft→transaction path**: it calls the same
`TransactionService.CreateAsync` as manual entry (`source = email`) inside one transaction with a
conditional `pending → confirmed` flip (donor ADR-0024 — the platform's `EfUnitOfWork` provides the
same savepoint nesting). The poller is an `IScheduledJob` (1-minute tick, connections due by their
own interval, "Sync now" on demand) running in **system context**: per connection it resolves the
owner's membership and `EnterTenant`s it, so staging writes get the same structural isolation as a
request. Outbound `HttpClient`s to Graph/Gmail/token endpoints are fixed hosts, allowlisted (R76).
*Rationale:* the user's mail is never mutated; idempotency comes from data; a misparse is a fixable
blank in a queue, never corrupt budget data; the platform already provides the scheduler,
encryption, isolation and unit-of-work this needs.

> **As built (P10a, 2026-09-03):** staging is `VoucherStagingService` in the Email slice; the connection is
> user-keyed, so the service resolves the owner's *current* household and `EnterTenant`s it before writing —
> RLS and the stamping interceptor scope the drafts. The poller is `EmailPollJob`, an `IScheduledJob` on the
> platform scheduler (no bespoke `BackgroundService`). The donor's cursor rules (transient hold, 7-day poison
> drop, saturation resume) carried over verbatim. A stored token that no longer unprotects flags the
> connection `needs_reconsent` instead of failing the cycle (ADR-V016).
>
> **As built (P10b, 2026-09-04):** the review queue and the merchant rules live in the same Email slice.
> Slices may not reference each other (R7), so "the same `TransactionService.CreateAsync`" became a **Core
> contract `ITransactionService`** (`Vuelto.Core.Budget`; `CreateTransactionCommand` carries the provenance
> `Source`) that the Ledger's `TransactionHandler` implements and `Program.cs` binds — the queue depends on
> the contract, never on the Ledger namespace. Confirm runs the create + the conditional `pending →
> confirmed` flip inside one `IUnitOfWork` scope; the loser of a concurrent confirm sees 0 rows, returns
> `not_pending`, and its scope disposes without commit so its transaction (and any month it created) rolls
> back. Discard is the same guarded flip. Merchant rules are matched by the pure `MerchantMatcher`
> (case-insensitive contains, longest wins) shared by staging and the slice; uniqueness per household is a
> stored lower-cased `PatternKey` under a unique index (the donor's functional `lower()` index, expressed in
> the EF model). The queue edits only the category and class (the owner's reversal of donor US-038);
> fields the parser could not read are the one exception and open for entry. A booked `email` transaction
> is editable and deletable like a manual one (`IsEditable` excludes only `refund_realization`).

**ADR-V011 — UI is rebuilt in the platform's Bootstrap RCL; MudBlazor is not brought over; brand tokens are indigo + gold. (2026-09-02; port decision D1)**
The donor's 6,382 razor lines (MudBlazor 9) are rewritten page by page, in slice order, as
Bootstrap 5.3 components in `Shared.Ui` — the platform deliberately carries no component library,
its shell/theme/MAUI hosts are Bootstrap, and the R68 "identical script set" gate makes a second
UI framework a permanent tax. The rewrite also retires the donor's client debt (43 duplicated inline
DTOs, 1,000-line pages) by decomposing pages into components. Brand: primary indigo `#5A67D8`, the
colón ₡ in gold `#F2CB6E` (**brand only — never a data/state color**), Nunito 800 wordmark, semantic
green/red for under/over budget, CRC/USD tints (`brand/BRAND-SPEC.md` in the donor is the source).
*Rationale:* one design system across web + native; no theme bridging; consistency with the platform
the app is now part of. Costs roughly a third more UI effort than keeping MudBlazor — accepted.

**ADR-V012 — API conventions follow the platform: minimal-API slices under `/api/<feature>`, camelCase JSON records, the shared `ErrorResponse`. (2026-09-02; port decision D5 — supersedes donor ADR-0005 snake_case)**
Every feature is a `MapTenantFeatureGroup` slice with a handler and co-located DTOs; no controllers,
no `[JsonPropertyName]`, no `api/v1` prefix. Error bodies are the platform's
`ErrorResponse(code, message)` — the same shape as the donor's `ApiError`, so the donor's error
codes (`exchange_rate_unavailable`, `refund_status_conflict`, `not_pending`, `*_exists_inactive`, …)
carry over unchanged. The Postman collection is the canonical API doc (ADR-023).
*Rationale:* the client is rewritten anyway, so the snake_case contract has no consumer left;
one convention across platform and app.

*Amendment (2026-09-02, BUDGET-1 — first slice).* Reading the platform's own DTOs settled the wire
format precisely: the platform serializes **snake_case via explicit `[JsonPropertyName]`** on every
DTO its clients consume (`in_app`, `member_count`, `created_at`, the Postman bodies) — the `Notes`
sample's bare records were the exception, not the rule. App slices therefore use
`[JsonPropertyName("snake_case")]` on request/response records (which also matches the donor's
ADR-0005 contract, so the ported client code keeps its field names). Everything else in this ADR
stands: minimal-API groups, plain handlers, the shared `ErrorResponse`.

**ADR-V013 — Hosting follows the platform runbook: Render (single origin) + Neon + Brevo via SMTP; the donor's Railway + Supabase + Vercel topology and Brevo HTTP sender are retired. (2026-09-02; port decision D3 — supersedes donor ADR-0027 and the ADR-0026 amendment)**
Email leaves through the platform's outbox → MailKit SMTP sender (Brevo as the relay). Railway
blocked outbound SMTP, which is why the donor grew a Brevo HTTP sender; Render does not, and the
platform's `render.yaml` + CI deploy hooks + version-gated smoke are proven. The single-origin
deployment also removes the donor's cross-origin cookie topology (SameSite=None, CORS from
`CLIENT_URL`). Donor ADR-0027's Supabase pooler / Railway builder gotchas are kept as history only.
*Rationale:* the platform's deploy story is the tested one; fewer moving parts than the donor's
three-host split. If a host that blocks SMTP is ever preferred, an HTTP email provider is a
**platform** feature (upstream), not an app fork.

**ADR-V014 — Critical state transitions use conditional updates + savepoint-nested transactions (inherited). (2026-09-02; from donor ADR-0024)**
Voucher confirm, refund status flips, month get-or-create retry and membership transitions gate
on a conditional `ExecuteUpdateAsync (… WHERE status = current)` and nest unit-of-work scopes; the
platform's `EfUnitOfWork` creates a savepoint when a transaction is already open and rolls back to
it on failure — identical semantics to the donor's. The donor's real-Postgres proofs
(`PendingVoucherConfirmIntegrationTests`, `RefundConcurrencyIntegrationTests`,
`EfTransactionScopeRollbackTests`) port unchanged and now run with RLS enforced.
*Rationale:* the in-memory value read earlier in a request is never the authority; only the
conditional write is.

*As built (P5b / LEDGER-3, 2026-09-03).* `RefundHandler.SetStatusAsync` opens an `IUnitOfWork`
scope, inserts the derived inflow (so the FK target exists), then issues the guarded
`ExecuteUpdateAsync(… WHERE Status = <the status this caller read>)`; zero rows means a concurrent
flip won — the scope disposes without commit, the inflow rolls back with it, and the caller gets 409
`refund_status_conflict`. The revert path claims the flip first and then removes the inflow (and an
emptied month) inside the same scope. Proven on real Postgres with two independent contexts racing
(`RefundSliceTests.ConcurrentMarkReceived_…`). The month get-or-create race, by contrast, needs no
scope: the transaction path's single `SaveChanges` + detach-and-retry (ADR-V005 as-built) is the whole
mechanism. Caveat carried as platform feedback F21: the platform's outer `EfTransactionScope` does not
clear the change tracker on rollback (only the savepoint scope does), so a handler that retried
*inside* a rolled-back outer scope would re-send stale entities — this slice never does.

**ADR-V015 — Rebrand as built: code identity `vuelto.*`, display brand "¿Y el vuelto?", app id `com.perezosoft.vuelto`, the platform's Data Protection strings kept, Notes sample deleted, own Docker stack. (2026-09-02; Guide Phase 3, per platform ADR-019 + `REBRANDING.md`)**
The platform's find/replace rebrand was applied end to end: solution `Vuelto.slnx`, every
project/assembly and root namespace `vuelto.*` (the RCL's static-asset path is therefore
`_content/Vuelto.Shared.Ui/`), JWT issuer `Vuelto`, OTel service name `Vuelto.Api`, native OAuth
callback scheme **`vuelto`** (`vuelto://auth` on Android/iOS/Catalyst, `Auth:Native:CallbackScheme`),
native env override `VUELTO_API_BASE_URL`. The **display brand** is *"¿Y el vuelto?"* everywhere a
person reads it (header wordmark, page titles, OG tags, MAUI `ApplicationTitle`, email sender name,
email copy in both languages, invitation/OTP/magic-link strings); the **code, DB and docs keep
`Vuelto`** — the split is deliberate (donor `brand/BRAND-SPEC.md`). Two identity choices worth
naming: (1) the MAUI **`ApplicationId` is `com.perezosoft.vuelto`** — reverse-DNS of the publisher
(Perezosoft) plus the app, so the one remaining `perezosoft` token outside history docs is the
publisher segment, by design (it also drives the APK name, the iOS bundle id in `ci.yml`'s
simulator smoke, the Android smoke's `PKG` default, and the Catalyst `keychain-access-groups`
entitlement — all updated together); (2) the MFA authenticator **issuer stays `Vuelto`** (an
`otpauth://` label; the inverted `¿` renders unreliably in authenticator apps). **Deliberately
unchanged, per ADR-019:** the Data Protection application name `"template"` and the
`CreateProtector("Template.*.v1")` purpose strings (key derivation). **Palette** (from the logo):
primary indigo `#5A67D8`, accent ring `#8B95F0`, dark/wordmark ink `#3A4178`, light accent
`#C5CBF7`, surfaces `#F7F7FC` / `#E1E3F2` (dark: `#14162A` / `#2C3050`); gold `#F2CB6E` exists only as
`--brand-gold` and is never a state color. **Typeface** Nunito via Google Fonts in both hosts'
`index.html` with a system fallback. **Assets** rendered from the donor SVGs (headless Chromium +
Pillow; generator kept outside the repo): RCL lockups (1520 px) + tile icon, CID email logo, web
favicons/OG/PWA set, MAUI adaptive icon (gold ₡ at ~58 % of the canvas) + coin splash, store/marketing
PNGs. **Removed:** the `Notes` sample slice (entity, configuration, `AddNotesSample` migration and its
row in the RLS backstop, feature folder, tests, arch-test/doc-sync entries, Postman folder 7) — the
model snapshot was edited by hand and the drift gate proves it. **Docker:** compose project
`vuelto`, host ports **5434 / 1026 / 8026** (Postgres / Mailpit SMTP / UI) so the platform's
own stack (5433 / 1025 / 8025) and the donor's Postgres (5432) can run alongside; `.env.example`,
the E2E/mobile docs follow; `appsettings.Development.json` keeps the platform default `1025` (CI maps Mailpit there) and `.env` carries the local `Email__Smtp__Port=1026` override â the first CI run of this PR failed all 31 OTP-based E2E tests when the port was checked in. Render service renamed
`vuelto-staging` **before** any deploy (the name is the public URL). Verification for this
repo: `git grep -i perezosoft` is clean outside `docs/DECISIONS.md`, `docs/REBRANDING.md`,
`docs/QA_TEST_PLAN.md`, `docs/audits/**`, `docs/stories/**` (platform history, left verbatim) and
the publisher segment of the app id — a tighter reading than `REBRANDING.md`'s "nothing outside this
doc" (platform feedback F14).
*Rationale:* ADR-019 makes the rename one unambiguous find/replace; keeping the publisher in the
app id and the platform's key-derivation strings avoids inventing identity where none is needed.

*Amendment (2026-09-02, same day — one identifier everywhere).* The owner asked for naming
consistency: the app's single identifier is **`vuelto`, cased by each context's own standard**: lowercase wherever lowercase is the norm (GitHub repo and folder, compose project, Render service `vuelto-staging`, app id `com.perezosoft.vuelto`, URL scheme `vuelto://`, kebab tokens such as `vuelto-logo`), and ordinary capitalization elsewhere â `Vuelto` as a proper noun in prose, titles and display strings (Swagger "Vuelto API", Postman "Vuelto â local dev", JWT/MFA issuer `Vuelto`), `Vuelto.*` for .NET identifiers and their files (`Vuelto.slnx`, `Vuelto.Api.csproj`, `Vuelto.postman_collection.json`, `_content/Vuelto.Shared.Ui`) â the same repo-vs-code convention as `perezosoft-platform` / `Perezosoft.*` â and
*"¿Y el vuelto?"* is used **only** where a person reads it. Consequences: the GitHub repo
`argamboad/y-el-vuelto` was renamed **`argamboad/vuelto`** (the frozen donor became
**`argamboad/vuelto-legacy`**; GitHub redirects both old URLs), the local folders follow, the compose
project is `vuelto` and the Render service `vuelto-staging`. `y-el-vuelto` no longer appears as an
identifier anywhere. Rejected alternative: `YElVuelto.*` namespaces — C# identifiers can't carry
hyphens, so "y-el-vuelto everywhere" would have introduced a fourth spelling. **Postman:** the
collection ("Vuelto API") and environments sync into the **same Perezosoft workspace** as the
platform's — one workspace for every downstream app, distinguished by name.

**ADR-V016 — Mail-consent secrets ride the platform key ring; the consent callback is the one anonymous feature route. (2026-09-03; port slice P9b, from donor US-026/US-037 + ADR-0016)**
The donor protected `EmailConnection` tokens with its own AES-256-GCM key (`EMAIL_TOKEN_ENCRYPTION_KEY`)
and signed the OAuth `state` with an HMAC secret (`CONSENT_STATE_SECRET`, falling back to the JWT
secret). On the platform both become **Data Protection** payloads — tokens under the purpose
`Vuelto.Mail.Tokens.v1`, the state under `Vuelto.Mail.ConsentState.v1` as a **time-limited** (15 min)
protector. The key ring is already persisted in the database and shared by every API instance, so there
is **no new secret** to provision, rotate or leak, and a payload from another purpose or key ring fails
closed. The consent apps are the platform's own `Authentication:Microsoft` / `Authentication:Google`
credentials (tenant default `consumers`, mirroring the login handler) — an unconfigured provider answers
`provider_not_configured`. The consent **callback** lives inside the `/api/email/connections` tenant
feature group with `.AllowAnonymous()` on that one endpoint: the IdP redirect carries no JWT, and the
signed, expiring state IS the authorization — the same shape as the platform's anonymous
`/api/files/{token}` (ADR-010). Everything else in the group keeps the tenant-API policy, and the entity
stays user-keyed (ADR-V002) with an `IUserDataContributor` for erasure. **Consequences:** the readers and
the consent service are R76-allowlisted senders with fixed provider hosts (Graph's `nextLink` is followed
only while it stays on graph.microsoft.com); `.env.example` gains nothing; a Data Protection key-ring
reset (never done in normal operation) would invalidate stored mail tokens — users simply reconnect.

**ADR-V017 — A realized refund is income in the month the money lands, not the month of the purchase. (2026-09-04; owner decision, supersedes the donor's ADR-0020 placement rule)**
The donor booked the derived `inflow` into the refund's own month with the purchase's date, so a
refund received in October raised September's income after the fact and October never saw the money.
Once the inflow became a real transaction (P5b) it should follow the rule every transaction follows:
its own date decides its month. `PUT /api/refunds/{id}` with `received` now takes a `received_date`
(default today; a date before the purchase is 400 `invalid_request`); the inflow is dated with it and
filed through the ordinary anchor-window resolution — the month is auto-created when needed, exactly
like a manual transaction, and retired on revert if that emptied it. The `Refund` row keeps its
purchase's month (so the purchase still explains itself there) and gains `received_date`; the API
reports `inflow_month_id` so the month page can label the row "Received on …" and link the month the
money landed in. Money semantics are unchanged: same amounts, the purchase's frozen rate, bank and
category; the dashboard already counted only pending refunds as informational and realized ones as
income wherever their inflow lives. *Rationale:* income should land in the month it arrives, and a
month already lived through must not change after the fact. *Consequences:* migration
`AddRefundReceivedDate` (nullable, no RLS change); `RefundHandler.SetStatusAsync` now runs the
month get-or-create inside its unit-of-work scope and reports a lost month-creation race as the same
409 `refund_status_conflict` the guarded flip uses (retry finds the winner's month); rows realized
before this ADR keep their purchase-month inflow and a null `received_date`.

**ADR-V018 — The dashboard's money story is one waterfall from income to the month-end forecast; the Expenses/Balance card pair and "remainder for debts" are retired from the UI. (2026-09-07; owner decision, supersedes the donor's US-005 three-card layout)**

The donor dashboard showed three cards — Income, Expenses (card / account / total / remainder) and
Balance (current balance / remainder for debts / pending budgeted / actual remainder) — and the owner,
reading his own September, could no longer tell which figure answered which question: "remainder" and
"current balance" were the same number twice, "remainder for debts" compared income with *budgets* and
never moved during the month, and the card/account split duplicated the bank × method table. **Decision:**
one card, *This month*, read top to bottom where every row is the previous one minus something:
Income → − Budgeted spent → − Discretionary spent → − Unplanned spent → = Spent so far → = Left now →
− Still planned → = Forecast at month end. *Still planned* keeps its definition (each active line's budget
minus its category's spend, floored at zero, in the line's own currency, valued at today's rate). The
forecast is red with a warning line when below zero; *Still planned* carries a pace hint ("N% of the
month elapsed") so a large planned figure late in the month reads as a warning. The forecast still assumes
only the plan is left to spend — future discretionary/unplanned purchases are not guessed (that would be
invention). *Consequences:* `ExpenseSummary` gains the class cut (`Budgeted`, `Extraordinary`,
`UnplannedEssential`), exposed as `spent_*` on the summary; the API keeps `remainder_for_debts`,
`expenses_card` and `expenses_account` (additive contract, the bank × method table and Postman still use
them) but the UI no longer shows the first; QA-DASH-01 rewritten. Reports' "Income vs spend" /
"Income vs budget" donuts are this card's picture twins and share the same Core calculators. *Same-day addendum:* the Income card is folded into the waterfall (Primary/Secondary as sub-rows) and a stacked bar heads the card — income-wide, the three class spends and Still planned filling it, the forecast as the green rest, Today marked, the shortfall red past the income — so proportions and pace are visible before the numbers are read.

**ADR-V019 — The day's rate is the Banco Central's buy/sell pair, and the side used follows the money the household would actually move. (2026-09-07; owner decision, amends ADR-V006's single-rate provider)**

The donor quoted one mid-market USD→CRC figure from a world feed (exchangerate-api.com, keyed, quota
of 1,500 calls a month). Costa Rican banks do not trade at that figure: on 2026-09-07 the BCCR
reference pair was **compra 448.27 / venta 453.69** while the feed said 453.28, so every colón
projection was a few colones off and a $ voucher was frozen at a rate no bank would have charged.
**Decision:** the default provider is the **BCCR reference pair** read from the Finance Ministry's
public mirror (`api.hacienda.go.cr/indicadores/tc` — no key, no quota; `ExchangeRate__Provider=bccr`,
`ExchangeRate__BccrUrl`), and the app carries **two rates**: `Buy` (compra — what the bank pays you
for a dollar) and `Sell` (venta — what a dollar costs you). **The one rule** (`FxRates` in Core):

| Money | Currency | Converts at | Why |
|---|---|---|---|
| Spending (a voucher, a manual purchase, a budget line) | USD | **Sell** | you would buy the dollars |
| Spending | CRC | **Buy** | you would sell dollars to fund it |
| Income | USD | **Buy** | the bank buys your dollars |
| Income | CRC | **Sell** | your colones buy dollars |

A transaction still freezes **one** rate (`exchange_rate_used`, ADR-V006): the side its own currency
selects — a $ voucher confirmed today freezes 453.69, a ₡ voucher 448.27. A single-rate source (a
manual override on the form, the last-transaction tier, the world feed kept behind
`ExchangeRate__Provider=exchangerate-api`) is the pair with both sides equal, so every rule collapses
to the old behaviour and no stored row changes meaning. The displayed "per $1" figure is the sell side;
the API adds `buy` / `sell` to `GET /api/exchange-rate` and `exchange_rate_buy` to the month summary;
the dashboard's Today's-rate badge reads "buy ₡448.27 · sell ₡453.69 per $1" when the sides differ
(the month header no longer repeats the rate); the new-transaction form pre-fills the side for the chosen currency and never overwrites a typed rate.
*Rationale:* the household's numbers should match what its bank statement will say, and a rule that
follows the direction of the money is the only one that does not need a footnote. *Consequences:* the
reference rate moves once a day (none on weekends/holidays — the last published pair is the current
one), so the one-hour freshness window is generous; the world feed stays selectable; the integration
harness pins the keyless world feed so the suite never reaches the mirror; QA-FX-01/02 rewritten.

**ADR-V020 — The "show amounts in" preference follows the person, in an app-owned user-keyed table. (2026-09-07; owner decision)**

The ₡ · $ · both switch shipped in PR #43 as a per-device preference (browser storage), and the owner
asked for it to follow him "like language" — the phone and the laptop should agree. The platform keeps
theme and locale on its `User` row behind `/api/auth/theme` and `/api/auth/locale`; that row is platform
code the app must not extend (the extend-never-modify rule). **Decision:** a small app-owned, **user-keyed**
table `UserDisplaySettings` (one row per user, `display_currency` CRC | USD | both) behind
`GET`/`PUT /api/display-settings`, wiped by account erasure through an `IUserDataContributor` — the
`EmailConnection` shape (ADR-V002), because a display taste belongs to the person, not the household.
The client keeps the **theme's two-copy model**: a device copy (`appUi` prefs) so a page paints before
any call, and the account copy that wins once the user has chosen there; a user who never chose on the
account adopts the device's choice and pushes it up, so nobody loses a setting on upgrade. A write from
an impersonation session is refused with the platform's `impersonation_not_allowed`, same as theme and
locale. *Consequences:* migration `AddUserDisplaySettings` (no RLS — user-keyed); Postman folder 23;
QA-DASH-03; the switch itself is unchanged for the user.

**ADR-V021 — Cards are a household catalog keyed by what the bank prints; a transaction may name one; "no card" is a bucket, not a row. (2026-09-08; owner request)**

The vouchers already carry the card: BAC prints `VISA ************1234`, BN `MASTERCARD ************0000`
or, on a payment receipt, an unlabelled `XXXXXXXXXXX0000X`. The parser captured the masked number and
dropped it at the review queue. **Decision:** a **`Card`** catalog entry per household — alias
(`Name`), `Brand` (VISA / MASTERCARD / AMEX / `CARD` when the voucher names none), `Last4`, optional
bank, soft delete, unique on the alias and on (brand, last four) — and an optional `card_id` on the
transaction. **Capture is automatic:** a voucher confirm resolves the card by brand + last four
(`ICardResolver`, the Cards slice's face for the review queue — R7) and creates it as **`VISA-1234`**
on first sight (`auto_named` until the household renames it — the alias is theirs, the identity is the
bank's). Manual entry offers the card as an optional picker. **Existing and card-less transactions keep
`card_id` null** and fall into a "no card" bucket wherever spend is cut by card — no LEGACY row, nothing
fake in the catalog. *Rationale:* the question "how much went through which card" needs a stable
identity the household never types (the bank's) and a label it recognises (its own); the two are kept
apart so renaming never breaks matching. *Consequences:* migration `AddCards` (RLS, `Transactions.CardId`,
`PendingVouchers.CardBrand`); `GET`/`POST`/`PUT /api/cards`; the month list and the CSV carry the alias;
the snapshot tool carries the table; the per-card summaries (dashboard "By card" table, Reports "Spend by card" bars)
shipped as CARDS-2 (2026-09-09) over one Core cut, `CardSpend.Calculate` — expense rows by `card_id`, "no card" last.
*Renewals (same day, owner question):* a renewed card prints a new number but is the same card, so a card owns a
**list of identities** (`CardIdentity`, unique per household) rather than one pair; the voucher path matches on any
of them, and `POST /api/cards/{id}/merge { into }` folds the auto-named newcomer into the card the household knows —
identities and transactions move, the duplicate goes, the survivor shows the newest number. History never splits.
*Brandless vouchers:* BN payments print no brand and drafts staged before brand capture carry none, so a brandless
number resolves to the card already known by those four digits, and a branded number that meets a `CARD`
placeholder upgrades it in place (`CARD-1966` → `VISA-1966` while still auto-named) — one plastic, one card.

**ADR-V022 — The report PDF is rendered by the API with QuestPDF, from the page's own figures, with its own SVG chart builders. (2026-09-16; owner decision, REPORTS-7)**

The owner asked for "a nice PDF report, with tables and charts" of the Reports page, and for the same file to
reach the inbox (REPORTS-8). **Decision:** the **API renders it with QuestPDF** (Community licence) and returns
it through the CSV export's delivery — stored with `IFileStorage`, a 15-minute signed link, the shared
`IFileDownloadLauncher` (browser download, native share sheet). *Alternatives rejected:* headless Chromium
printing a server-rendered page (hundreds of MB and a browser process on the Render free tier's small
instance) and the browser's Print-to-PDF (no branded header/footer, output varies by browser, nothing in the
MAUI shells). *Same numbers as the screen:* a pure `ReportPdfModelBuilder` turns exactly what the page reads —
the category analysis, the months trend, the month's pending refunds, and `ExportRowsAsync` (the CSV's rows,
now shared by both files) — into formatted strings and SVG, mirroring `Reports.razor` rule for rule; QuestPDF
only lays it out. *Charts:* the plan was to lift the web charts' geometry into Core and share it; the UI library
references neither the API nor Core (its DTOs are hand-mirrored by design), and adding that link to share ~150
lines of arc and bar math was not worth a new structural dependency, so the PDF has **its own builders**
(`PdfCharts`) with the web geometry and literal light-theme colours — a PDF has no stylesheet, so no `var(`
survives, and every label is XML-escaped (names are user data). Dashes are drawn as segments because the SVG
engine paints `stroke-dasharray` gaps. *Fonts:* system fonts are off so every host renders the same file;
Nunito (OFL) is embedded as static 400/600/700 faces cut from the variable font, and it **carries ₡**, so the
owner's "write CRC if the symbol is missing" fallback was not needed; a glyph Nunito lacks (an emoji) falls back
instead of failing the report. *Spike (2026-09-16):* QuestPDF 2026.9 renders in `mcr.microsoft.com/dotnet/aspnet:10.0.11`
with **no extra apt package**; custom font names are no longer supported, so the faces carry the family name
themselves. *Consequences:* `POST /api/reports/pdf` (JSON body: period, `display`, `chart_currency`,
`include_appendix`, `language`, `today`; 400 `invalid_request` on an unknown value); the response has no page
count (QuestPDF reports none without rendering twice); Letter portrait for the report, landscape for the
appendix (every CSV column); new resx pair `ReportPdfStrings` under the resource-parity gate; the brand lockup is
linked from `Shared.Ui/wwwroot/brand` so the brand keeps one file. REPORTS-8 mails the same bytes
(`ReportPdfHandler.RenderAsync`) as an attachment through the platform's `IEmailSender` attachment seam.

*Amendment (2026-09-17, REPORTS-9, owner):* the appendix's columns are the reader's choice — `appendix_columns` on both
PDF endpoints, any of category, class, amount, rate, method, bank, source, card, notes, printed in the usual order;
date and payee always print; absent means all (the file is unchanged for existing callers); an unknown key is a 400.
The dialog ticks every column each time it opens rather than remembering a choice — a leaner file is a per-report
decision, and a remembered one would silently drop columns from the next report.
*Amendment (2026-09-16, REPORTS-8 as built):* **"Email me"** is `POST /api/reports/pdf/email` — one email, queued through
the outbox, to the caller's own address (no recipient input), the PDF attached, the body the platform's generic branded
notification (`BrandedEmail.Notification`, HTML-encoded) naming the period, the household and the total spend; nothing is
stored. The attachment seam (JOBS-4, platform #235) was synced inside the same commit. **Language, owner question:** both
PDF endpoints speak the language **saved in the account settings** (`User.Locale`, English when unset or unsupported);
an explicit `language` still wins for API callers, and the app stopped sending one — so the file follows the setting on
every client, including a device whose UI hasn't reconciled yet. **Cap (plan A9):** 10 report emails per person per day
through an app-registered rate-limit policy (`ReportEmailRateLimit`, added with `Configure<RateLimiterOptions>` on top of
the platform's `AddApiRateLimiters`, which stays untouched; per user, fixed window, in memory — a restart resets it,
acceptable for guarding the shared email quota against loops). A file over the 10 MiB attachment limit answers 400
`report_too_large` before anything is queued.

**ADR-V023 — Income is a list of household income lines with a pay period; each month snapshots them into its own editable rows. The old 4w/5w and primary/secondary fields are retired but kept. (2026-09-16; owner decision, INCOME-1)**

The donor model gave a household exactly two incomes, each typed twice (a four-week and a five-week figure), and a month
two amount-plus-currency slots. A household has members, each with their own incomes, paid on different rhythms: a weekly
salary really is 4 or 5 transfers a month, a monthly salary is the same every month whatever the week count, and a
quincena lands twice. The owner asked whether income should be transactions instead; **decision:** no — the budget needs
the income *plan* before the money arrives, so income stays a planned figure per month, and money that actually comes in
unplanned keeps being an `inflow` transaction folded into income (unchanged).
**Model.** `IncomeLine` (household catalog, ADR-V008 rules: unique name case-insensitively, soft delete, 409 reactivation
offer, ordered): name, optional member (no FK), currency, kind `fixed | variable` (variable = an estimate to correct),
pay period `weekly | biweekly | monthly`, amount **per payment**, two pay days for biweekly only (default 15 and the last
day, stored as 31 and clamped). `MonthIncome` rows belong to a month (cascade): label, member and currency **copied** from
the line (a rename never rewrites history), an editable `amount`, and the `planned_amount` the pay period derived, null
for a one-off row added to a single month. *Snapshot rule* (`IncomeSnapshot`, pure, Core): at month creation one row per
active line; weekly × the month's stored week count, biweekly × the pay days inside [first week start, last week end],
monthly × 1. *Month total* (`IncomeCalculator`): each row at the day's rate by the income direction (USD at buy, CRC at
sell) plus inflows. **The payday is the week start:** the anchor is "last week-start day of the previous month", so for
a weekly-paid household the week must start on the day the money moves for week count to equal transfer count — the
settings card says so.
**Migration, expand only (plan §4a).** `AddIncomeLines` creates the two tables with the RLS policy and copies: lines from
`BudgetSettings` (5w = 4w → monthly; 5w/5 = 4w/4 → weekly at 4w/4; otherwise weekly at 4w/4 flagged `needs_review`, which
any update clears; a zero slot makes no line) and one row per non-zero month slot with the stored amount copied
**verbatim** into both `amount` and `planned_amount` — history is never re-derived. Deterministic ids and `NOT EXISTS`
guards make it idempotent; the same SQL ships as `ops/backfill-income-lines.sql` (a test keeps them identical) for a
pre-migration snapshot restored later, and `ops/check-income-parity.sql` compares old and new totals per month and
currency. The migration sets `app.rls_bypass` itself, because Neon's owner role is not a superuser and FORCE RLS would
otherwise hide every row. `Down` drops only the new tables.
**Kept, not dropped.** The six `BudgetSettings` and four `Month` income columns stay mapped and untouched — the rollback
baseline. An architecture test fails if any code other than the entities, their mappings, the backfill and the migrations
names them. Dropping them is INCOME-3, a later owner-gated contract migration.
*Consequences:* `/api/incomes` (list, create, update, `PUT /order`); `GET /api/months/{id}` carries `income_rows`;
`PUT /api/months/{id}/income` takes the month's full row list (with an id updates, without one is a one-off, left out is
removed); `budget-settings` lost its income fields; the dashboard summary reports `income_lines` + `income_inflows` instead
of primary/secondary. The account-erasure contributor clears `member_user_id` on lines and rows, amounts kept.
*Amendment (2026-09-17, owner):* **whose income it is follows the line.** A month's rows stay copies for their
label, amount and currency, but when a line's member changes, the rows copied from it that still carry its previous
member take the new one, in the same save. Without this, the migrated rows (no member) could never be attributed —
the month page edits no member — and the reports' income by member read "the household" for every past month. A row
whose member differs from the line's previous member is left alone.
*Amendment (2026-09-16, INCOME-2):* the reports cut the month's income by whose it is (`IncomeByMember` in Core; the
analysis response's `income_by_member`, printed as a table in the PDF; the first cut's donut on the page and in the PDF
was removed on 2026-09-17 at the owner's request — the table says it). Rows of a member who left
form one "former members" slice with no name — a departed member's name is not kept in this household's reports.
*Deviation from the plan:* a member who leaves is **skipped at snapshot time** (the member is no longer in the household)
rather than having their lines deactivated in storage — the line keeps its member for history and can be reassigned; the
edit form still shows the former member. *Supersedes:* ADR-V003's income defaults and ADR-V005's "two incomes per month".

**ADR-V024 — A voucher is booked at the rate of the day it arrived, not the day it is confirmed. (2026-09-22; owner decision, EMAIL-8)**

Confirm passed no rate, so the ledger resolved today's (ADR-V006): a voucher's date set its budget month, but a Friday
purchase reviewed on Monday was converted at Monday's rate — and the frozen rate can never be corrected afterwards.
**Decision (the owner's pick of two):** staging freezes the day's **live** buy/sell pair on the draft
(`PendingVoucher.StagedRateBuy/Sell/AsOf`, fetched once per sync and only when something is staged); confirm passes the
side the booked currency selects (ADR-V019) as the command's rate. No live quote at staging (provider down, only a stale
cache) → no staged rate → confirm resolves today's as before. **Rejected:** looking up the historical rate for the
voucher's date at confirm — more exact, but it needs a dated source in the provider tier (the BCCR mirror's history is
unverified) and a new tier in ADR-V006's chain. **Trade-off:** the staged rate is the rate of the day the mail was
*read*, which is the purchase day for the normal sync cadence but not for a backfilled folder or a re-read queue
(EMAIL-7); it is never later than confirm, so nothing gets worse. *Amends:* ADR-V006 (a voucher's rate is resolved at
staging, not at confirm) and ADR-V010 (confirm still books only through `TransactionService.CreateAsync` — now with a
rate). Migration `AddPendingVoucherStagedRate` adds three nullable columns and drops nothing.

**ADR-V025 — A refund expectation is an attribute of unplanned and discretionary spending, not a class of its own. (2026-10-09; owner decision, LEDGER-5, #201)**

The owner asked for refunds on discretionary spending and whether "expecting a refund" should be a new class.
**Decision:** no sixth class. The class answers *which part of the plan this money came out of*; a refund answers *will
some of it come back* — a discretionary purchase you will be reimbursed for is still discretionary spend, and a new
class would force a choice between two true things and touch every per-class cut (the dashboard waterfall, the weekly
cuts, reports, the PDF, the CSV, budget-line backing). A refund now rides on `unplanned_essential` **and**
`extraordinary`; **not** on `budgeted` (the owner: you don't budget for money you expect back), and never on money
in or set aside (`TransactionTypes.RefundClasses`). The flag on another class is a **400** `invalid_request` — it was
silently ignored, and a refund the household asked for must never quietly vanish. The dashboard summary splits the
pending refunds by the transaction's class (`unplanned_refunds`, `discretionary_refunds`), so each Reports/PDF tile
shows its own "refundable" figure; `refunds_total` stays their sum. *Amends:* ADR-V007 (refunds were derived from
unplanned essentials only). No schema change.

**ADR-V026 — A refund is stored as its amount; a percentage is only a way to type it. A received refund is locked. (2026-10-09; owner decision, LEDGER-6, #202)**

The owner asked for refunds as a percentage **or** a fixed amount, then settled the shape: *"the amount is what should
be stored, the percentage if used should be only used in the moment to calculate the amount."* **Decision:** the API
takes `refund_amount` in the transaction's own currency (`0 < amount ≤ original_amount`, 2 dp); the other currency is
derived at the transaction's frozen rate with the same `CurrencyMath.DeriveAmounts` the purchase uses, so a full
refund matches it to the cent (golden rule 1). The form offers a ₡ | % toggle; in % mode it computes the amount from
the purchase (and follows the purchase while you're still in % mode), and an edit opens on the stored amount with the
share it comes to as a hint — correcting "by percentage" later is the form computing a new amount from the purchase's
current amount. A transaction amount edit **no longer rescales** a refund; a purchase below its refund is a 400.
`Refunds.Percentage` becomes nullable and is no longer written (an edit nulls it); a percentage on screen is computed
from the amounts (`RefundResponse.percentage`). **Received refunds are locked** (owner, 2026-10-09 — this is new:
before, an edit silently re-derived a received refund and rewrote its booked inflow, and a class change deleted both):
changing its amount, clearing the flag, moving the purchase to a class that can't carry it, or deleting the purchase is
`409 refund_status_conflict` until it is put back to pending — which stays allowed and removes the inflow (owner-
confirmed: the way to fix a mistake). Edits that don't touch the refund still save; the inflow is never rewritten.
*Data:* migration `RefundPercentageOptional` only relaxes the column (every existing refund keeps its amounts and
percentage — nothing is recomputed); its `Down` refills an emptied percentage from the amounts against the purchase.
Dropping the column is a later, owner-gated step. *Amends:* ADR-V007 (amounts = percentage × the transaction) and
LEDGER-3 (a realized refund's inflow tracked the re-derived amounts).

**ADR-V027 — A masked card number whose digits are not the last four is asked about once and remembered, never guessed. (2026-10-09; owner request, CARDS-4, #210)**

BN "pagos" receipts print the card as `XXXXXXXXXXX8755X` for the owner's Black card, which ends **7558**: the visible
digits are not the last four. `CardIdentity.Last4` took the last run of digits and kept its final four, so every BN
payment was booked on an auto-created `CARD-8755`, and the confirm had no way to say otherwise. **Decision:** one rule
in Core, `CardIdentity.Read` — spaces and dashes are layout, any other non-digit is a mask; digits that close the
number are its last four, digits **followed by a mask** are a **pattern** (normalized: upper-case, every mask an `X`)
with no last four. A pattern is never matched or auto-created by digits. The household maps it once: the review queue
shows the pattern (`card_label`), says a card must be chosen (`card_ambiguous`), and offers the household's cards and
"No card"; the confirm takes `card_id` (an active card of the household — 400 otherwise) or `skip_card`, and a
`card_id` on a pattern is remembered in a new `CardPattern` table (tenant-scoped, RLS, unique per household, moved by
a merge, exported and wiped with the cards). The next voucher printing that pattern books on that card and the queue
preselects it (`known_card_id`), where it can still be changed — a different answer replaces the old one. A plain
number keeps CARDS-1's behaviour (found by brand + last four, created as `VISA-1234` on first sight). **Rejected:**
assuming BN hides the final digit and rebuilding the last four (`8755X` → `…755?`) — the owner's case shows the
digits do not line up with the plastic's last four in any fixed way, and a wrong guess silently books money on the
wrong card; and a per-bank rule — the mask, not the bank, is what says the digits are not the last four. *Existing
data:* payments already on an auto card are moved with the existing merge on `/cards`; no migration rewrites history.
Migration `AddCardPatterns` adds one empty table. *Amends:* ADR-V021 (identity = brand + last four, now only when the
number ends in its digits).

**ADR-025 — (number reserved; never adopted) CI runner selection is variable-driven with a hosted fallback (LOCALCI-1). (drafted 2026-09-08)**
*Stub.* A platform draft (it lives in the perezosoft-platform repo, `docs/stories/localci.md`) that was never
adopted: ADR-028 replaced the design before it was built, and ADR-030 retired self-hosted CI. The number
stays reserved so the platform's numbering and this file's agree; do not reuse it.

**ADR-026 — (draft, not yet adopted) The platform becomes the reference implementation of a stack-neutral spec (FLAVORS). (drafted 2026-09-08)**
*Stub.* A platform draft (perezosoft-platform, `docs/stories/flavors.md`), pasted into the platform's log when
its FLAVORS program starts. Nothing is decided, and nothing in it binds this app.

**ADR-027 — Pre-launch gates: billing and account creation are deployment configuration, not runtime switches (GATES-1/2). (2026-09-11)**
A deployment must be able to run **private and free** before it is published: nothing offers to sell
a tester anything, and a stranger who finds the URL cannot create an account. Hiding the deployment
itself was explicitly **not** wanted — knowing the app exists is fine.

Both properties are decided before the process starts and change exactly once, on launch day. That
makes them configuration, and it puts them in the same family as the PUBAPI/HOOKS gates
(ADR-015/016): default off, opted into per deployment.

**Decision:**
1. **`Billing:Enabled`, default off.** With the gate off the billing controllers and the provider
   webhook are removed from the application model at startup, so the routes do not exist (404) rather
   than existing and refusing. The client hides the billing link and refuses the route, but the API
   remains the authority. No new economics code is needed: `PlanCatalog.Get` already falls back to
   Free for an absent plan key, so with billing off **every tenant without a granting subscription is
   Free** — and nobody can be *given* one (the routes, the webhook and the staff comp are gone).
   *(Corrected 2026-09-28, v4 T46: the original text said "every tenant is Free"; plan resolution is
   gate-blind by design — a granting row, a comp or a Stripe row from before the gate closed, keeps its
   plan so that flipping the gate never silently downgrades anyone. `BillingPostureCheck` warns at startup
   when Stripe-managed rows linger while off, since the webhook that would cancel them is gone.)*
2. **`Signup:AllowedEmails` / `Signup:AllowedDomains`, empty means open.** Non-empty restricts
   account creation. The green list decides **who may found a household**; inside a household owned
   by a green-listed person, membership is that owner's business, bounded by the seat cap.
3. **The invitation bypass keys on the household's OWNER, not the inviter.** A valid pending
   invitation addressed to the caller admits them when that invitation's tenant owner is
   green-listed. Checking the owner (rather than `InvitedByUserId`) is what lets a non-listed admin
   member invite into their owner's household, while keeping a household whose owner is not listed
   from admitting anyone new.
4. **The gate fires at account creation only, never at sign-in.** It lives at the single choke point
   `UserService.CreateUserWithTenantAsync`, which every user-minting path funnels through (magic
   link, OTP, web OAuth, native OAuth). Gating sign-in would lock out people who already have data
   the moment the list is edited.
5. **It deliberately does not fire when a magic link or OTP is issued.** Refusing at issue time would
   require knowing whether the address already has an account, turning the login form into a "does
   this person use the app" oracle. Accepted cost: a non-listed visitor learns they are not invited
   only after entering their code.

**Addendum (2026-09-28, v4 audit T45 — BILL-2/BILL-3/ADV-P4-6, R86):** the proof behind decision 1 is the
**route table**, not a list of controllers. `BillingGateConvention` still removes the two billing controllers,
but what CI holds is `BillingGateTests.GateOff_NothingUnderTheGatedPrefixes_IsMapped`: the host booted at the
shipped defaults maps nothing under `api/billing`, `api/public`, `api/apikeys` or `api/webhooks`, by any
mechanism (a minimal-API group, an action-level absolute route, a differently named controller — the
adversarial pass had a billing-prefixed group answering 200 next to a 404 on `/api/billing`). The relaxed
Stripe startup check (the fake provider is tolerated outside Development while the gate is off) depends on
exactly that proof. The staff **comp/revert** actions under `api/admin` are part of the surface: they answer
**404 while billing is off**, before the staff check, and the console's block follows `GET /api/features`.

**Addendum (2026-09-28, v4 audit T46 — LB-BILL-21/22/27, R128):** the Stripe mapping **fails safe and
loud**. A missing `current_period_end` (Stripe.net's field is a non-nullable `DateTime`) is stored as null
with a Warning, not as the converter's sentinel date (the Unix epoch — which read as a lapsed period: a paying
tenant on Free, "renews on 1970-01-01", nudged every six hours). A price absent from `Billing:Stripe:Prices` is acknowledged, **not
applied**, and logged as an Error — never an "active Free" row staff cannot comp around. An event whose
`livemode` disagrees with the deployment's expectation (`Billing:Stripe:ExpectLiveKey`, else inferred from
the key prefix) is ignored with a Warning, so a test-mode signing secret in production cannot let Dashboard
test events move real tenants' plans. Signed fixtures drive all three (`StripeBillingProviderTests`).
6. **The Free seat limit moves 3 → 5** (`PlanCatalog`, code/config per ADR-006). Three is exactly one
   family with no headroom, and a pending invitation already consumes a seat.

**Rejected — a runtime toggle on the `/admin` console.** It would need a platform-scoped settings
table, cross-instance cache invalidation, an audit trail and an admin write endpoint (which ADR-021
requires be enumerated), and it would downgrade the gate from "the route does not exist" to a
per-endpoint runtime check that every future billing endpoint must remember to run. It is also a
footgun once real subscriptions exist: switching billing off does not stop the provider charging.
The console may display the state **read-only**.

**Rejected — promotional codes attached to an email address.** A code delivered to an inbox proves
nothing that the passwordless sign-in to that same inbox does not already prove, so it would buy a
code lifecycle (issuance, redemption, single-use, expiry, revocation) for no additional security.

**Known and accepted:** `TenantService.ReHomeAsync` gives any departing member a fresh tenant-of-one
they own, so someone who arrived by invitation can end up owning a household. That household's owner
is not green-listed, so it admits nobody new — the leak is cosmetic, not a hole. *Same family, added by v4
T64 (AUTH-8, decision #3, 2026-10-01):* an invited-but-unlisted person **founds** their household of one at
sign-in, before accepting anything — `CreateUserWithTenantAsync` is the single choke point and it always
provisions tenant + owner membership. If they never accept, the invitation is revoked, or accepting would
abandon data they have since created (`WouldAbandonData`), that household stays, owned by someone the list
never named. Deferring the household until the invitation is accepted was considered and rejected: it would
give every path that mints a user a second shape (a user with no tenant), which the tenant filter, the
JWT's `tenant_id` claim and the RLS backstop all assume away. The household admits nobody new, same as
above; it is a cosmetic orphan, not an access path.

**Ports downstream** (`vuelto`, `jigger-jot`) once the platform suite is green, like LOCALCI-3.

> *Numbering note: 025 and 026 are reserved upstream for platform-only work (local CI, the flavors program) that this app does not carry, so the sequence jumps.*

**ADR-028 — The self-hosted Forgejo is the primary forge and runs the full CI/CD; GitHub stays a mirror whose own CI runs only when pushed to on purpose (LOCALCI-4). (2026-09-16) — SUPERSEDED by ADR-030 (2026-10-02)**
The maintainer moved day-to-day git to a private Forgejo on the Windows desk (WSL2 + Docker, reachable over
Tailscale) so that routine pushes cost nothing and leave nothing on a third-party server. GitHub keeps the
repository — Render builds from it, and pushing there on purpose still runs the GitHub pipeline unchanged.
The Forgejo pipeline must do everything the GitHub one does, deploys included. This supersedes
LOCALCI-1's approach (route GitHub Actions to self-hosted runners) for this repo: the runners belong to
Forgejo, and GitHub keeps its hosted ones.

**Decision:**
1. **Two workflow files, held together by a test.** `.forgejo/workflows/ci.yml` is a copy of
   `.github/workflows/ci.yml` (each forge reads only its own directory once `.forgejo/workflows` exists).
   `ForgejoCiParityTests` (**R80**) fails when the job list, a `runs-on` line, a pinned version or the change
   classifier differs; the LOCALCI-3 gate tests run against both files. Every deliberate difference is
   marked `LOCALCI-4:` in the copy.
2. **The runners carry the hosted labels.** `ubuntu-latest` is a container image
   (`forgejo-ci/ubuntu:24.04`: catthehacker's act image + Docker CLI, PowerShell, `gh`, JDK 17, the Android
   SDK with the smoke's system image, the pinned .NET SDK and Android workload) on the WSL runner, which runs
   jobs inside Docker-in-Docker with host networking and `/dev/kvm` passed through. Every WSL job shares that
   one network namespace, so the two jobs that bind fixed ports (`e2e`, `native-smoke-android`) use
   `ubuntu-host-ports` — the same image on a second WSL runner that takes one job at a time, which also
   serializes them across runs and repos. That is the only `runs-on` difference the parity test allows, and
   it fails for any Linux job with service containers left on the shared runner. `windows-latest` is the
   Windows desk in host mode (a logon task, so WebView2 has a session). `macos-26` will be the MacBook.
   Identical labels are what make `runs-on` comparable line for line.
3. **Apple jobs require `vars.CI_MACOS_RUNNER`.** A job whose label no runner carries queues forever on
   Forgejo (GitHub would expire it in 24 h), so until the Mac is registered the Apple legs are skipped.
4. **Deploys publish to `deploy/*` branches on GitHub.** Render only builds from GitHub, and GitHub's
   `ci.yml` only triggers on `main`, `develop` and pull requests. The deploy job force-pushes the tested
   commit to `deploy/staging` (or `deploy/prod`) with a token scoped to that repository, fires the Render
   hook, and runs the same `deploy-smoke.sh`. Each Render service tracks its `deploy/*` branch with
   auto-deploy off. No GitHub Actions run for a deploy.
5. **Prod is a manual dispatch.** Forgejo has no Environments and no required reviewers. `deploy-prod` runs
   only on `workflow_dispatch` from `main`; a push to `main` runs every gate and deploys nothing. The
   dispatched run re-runs the gates first (`changes` fails open without a diff base).
6. **GitHub's deploy hooks are removed from GitHub.** With both pipelines holding the hook, a deliberate push
   to GitHub's `develop` would fire a second deploy of whatever `deploy/staging` holds. Without the secret,
   GitHub's deploy jobs already skip with a notice (DEPLOY-3's opt-in design) — a settings change, not a
   code change.

**Consequences.** The desk is now a deploy path: deploys need the laptop on (the accepted trade-off of
SETUP.md; GitHub stays the emergency route — restore the hook secret there and push). Runners are not
clean machines: the smoke's Postgres is recreated per run, port-binding Linux jobs queue behind each other,
and the image's SDK/workload pins join the CLAUDE.md
bump-together playbook. The Render deploy-hook and the GitHub mirror token live in Forgejo's secrets only.

**Rejected — one shared, forge-aware `ci.yml`.** It would remove the duplication but thread
`github.server_url` conditions through the file GitHub runs today, for every future reader of both.
**Rejected — pushing `develop` to GitHub to deploy.** It runs the whole GitHub pipeline, and its deploy job,
on every deploy. **Rejected — image-backed Render services.** Faster builds, but it means new services, a
registry account, and losing the "redeploy from GitHub" escape hatch; revisit if Render's build time bites.

**Addendum (2026-09-16, same day, after the first green runs) — deploys and smokes are on demand; the
deploy pushes the real branch to GitHub; points 4–6 above are superseded.** Two things became clear once
the pipeline ran on one machine: (a) a develop push that runs the emulator and the WebView2 smoke every
time costs 20 minutes of a laptop the maintainer is also working on, for legs that rarely change; and
(b) keeping GitHub deploying too was wanted after all ("I don't mind if GitHub deploys again when I push"),
which makes the `deploy/*` branches pointless — Render can follow only one branch, and it stays on
`develop`. So:
- **Pushes and PRs run the gates, `e2e` and the native *builds* only** (≈10 min). The native **smokes**
  run from `workflow_dispatch` (`smokes` = windows | android | apple | all) and on the Monday schedule.
  The builds stay per push on purpose: they are free in wall clock and catch compile rot within one merge.
- **Deploys run only from `workflow_dispatch`** (`deploy` = staging on `develop` | prod on `main`), never on
  a push, and only after every gate and every smoke *selected in that run* is green (a smoke not selected
  is skipped, and a skip is not a failure). Staging no longer tracks `develop` automatically: the
  maintainer decides when.
- **The deploy pushes the commit to the same branch on GitHub** — a plain fast-forward, never forced; if
  GitHub is ahead the deploy stops and says so — then fires the hook and runs the shared smoke. GitHub's
  pipeline runs on that push and re-deploys the same commit; accepted as harmless. GitHub keeps its hook,
  Render keeps `develop`, nothing to reconfigure. "The deploy from Forgejo" and "the push to GitHub" are
  the same button.
- The earlier rejection of "pushing `develop` to GitHub to deploy" is withdrawn: its cost (GitHub's run
  and second deploy) was re-weighed against the operational simplicity and accepted.

**Addendum (2026-09-22) — a commit that is already green can be deployed without re-running its gates.**
Three repos now share one desk. A deploy dispatch re-ran every gate on a commit whose merge run had gone
green minutes earlier: ~10 minutes of a laptop the maintainer is also working on, for an answer already
known. `.forgejo/workflows/deploy.yml` (dispatch only, Forgejo only) deploys such a commit directly — but it
does not take anyone's word for it: it asks this instance's own Actions API which jobs ran for THAT commit
and requires `changes`, `build-test`, `secret-scan`, `qa-artifacts`, `license-scan`, `docker-build`, `e2e`
and both `native-build` legs to have a success. A missing job, a failure, a docs-only run that skipped the
gates, or an API it cannot reach all stop the deploy and point back to `ci.yml`'s `deploy` input, which
runs the gates itself. The job token can read that API and write nothing (probed 2026-09-22).
Publishing is unchanged and shared: `push-to-github.sh` (fast-forward, never forced), the Render hook, then
`deploy-smoke.sh`. Native smokes are not required, exactly as in `ci.yml`'s deploy jobs — the Monday
schedule and the `smokes` input remain how those are proven. `ForgejoCiParityTests` pins the guard, the
dispatch-only trigger and the shared script.
**Rejected — a `skip_gates` input on `ci.yml`.** Same saving, but it would thread "was this skipped?"
through every gate's `if:` and every deploy `needs:`, and one careless edit would turn the deploy's
"everything green" rule into "nothing ran". A separate file cannot weaken the existing one.

**Ported to vuelto (2026-09-18).** Forgejo repo `argamboad/y-el-vuelto` (the GitHub mirror keeps its
name, `argamboad/vuelto`); Render `vuelto-staging` stays on GitHub's `develop` with auto-deploy off, so
the Forgejo deploy and a deliberate `git push github develop` are its only triggers. Runbook:
`DEPLOYMENT.md` §10. The Forgejo copy of `ci.yml` was built by applying the platform's GitHub→Forgejo
changes to this repo's own `ci.yml` (a clean three-way merge); `ForgejoCiParityTests` and the
LOCALCI-3 gate theories hold the two files together here too.

**ADR-030 — GitHub is the forge again; Forgejo is retired (supersedes ADR-028). (2026-10-02)**
Forgejo ran on the maintainer's work laptop, and a reboot or a Docker Desktop restart could stop CI
(the 2026-10-02 morning: the Linux runner's Docker-in-Docker had restarted 187 times). An always-on
machine at home is not in sight, so the forge goes back to GitHub:

1. **`origin` is GitHub** (private) for code, PRs, merges and issues. The Forgejo issues were migrated
   with their comments, labels and milestones; each GitHub issue's footer names its Forgejo number,
   which old commit messages cite.
2. **Forgejo is archived read-only, its runners are stopped.** It stays up as the rollback until it is
   switched off; its encrypted backup is the frozen copy.
3. **The repo is private, so every Actions minute is billed** (Free plan, 2,000 minutes a month, a $0
   budget that stops runs at the limit). On 2026-10-01 one deploy round cost ~600 minutes, so the `CI`
   workflow is **disabled until a CI rebuild**, whose agreed direction is: PRs and merges run backend +
   web UI only (build-test, the scans, docker-build, e2e); every **device leg** (Windows, Linux-native,
   macOS, iOS, Android builds and smokes) and every Render deploy runs only from a manual *Run workflow*.
   The rebuild gets its own ADR (it amends NATIVE-1 and LOCALCI-3) and deletes `.forgejo/`,
   `ForgejoCiParityTests` and R80; until then they stay, unused.
4. **No branch protection:** a private repo on GitHub Free has neither branch protection nor rulesets.
   Decided in ADR-031: none, the repos stay private.

**Consequences.** DEPLOYMENT §10 and the LOCALCI-4 story are history. The Postman workspace sync runs
from GitHub on every `develop` change. The platform and its downstream apps share this ADR.

**ADR-031 — CI on billed minutes: pull requests run the web gates only; the device legs and every deploy run on request. (2026-10-02)**
The repos are private (ADR-030), so every Actions minute is billed against 2,000 a month, and one develop push
used to cost 57–147 of them (macOS bills 10×, Windows 2×). The maintainer manages the minutes by hand, and the CI
says so:

1. **Triggers:** `pull_request` into `develop`/`main`, and `workflow_dispatch`. No push trigger — a merge runs
   nothing, its pull request already did — and no schedule.
2. **A pull request runs the web gates only:** `changes`, `secret-scan` and `qa-artifacts` always (a docs-only
   change can leak a credential or desync the QA PDFs), and `build-test`, `license-scan`, `docker-build`, `e2e`
   when code changed. e2e runs as ONE shard: on hosted runners every shard boots the stack again, and that
   setup cost more than the wall-clock it saved.
3. **The device legs run only from *Run workflow* → `devices`** (android / windows / apple / all): the MAUI
   builds, the Release APK and the three native smokes. This amends NATIVE-1 (ADR-018: "a change that breaks
   the native build fails the PR") and LOCALCI-3's per-push Apple build: a change that can affect the MAUI app
   is no longer proven by its PR. The maintainer runs them periodically and before a release. There is no
   Linux desktop flavor (MAUI has no Linux target; the web app and the Flavors fronts cover it).
4. **Deploys run only from *Run workflow* → `deploy`:** staging from `develop`, prod from `main` (a wrong
   branch fails loudly). That run re-runs every web gate and deploys only if all pass — deploys are rare, and
   re-testing beats trusting a merge commit. Choosing `prod` is the approval: GitHub Free has no Environment
   reviewers on a private repo. The device legs are not in the deploy's needs (a native result must not block
   a web deploy, ADR-018).
5. **Cost hygiene:** `timeout-minutes` on every job, a NuGet cache keyed on the lockfiles, 3-day artifact
   retention, and a new push to a pull request cancels its previous run.
6. **No branch protection** (ADR-030's open point, decided): the repos stay private on GitHub Free.
7. **`.forgejo/` is gone**, with `ForgejoCiParityTests`, the Forgejo branch-protection script and the
   Forgejo-only CI-logic targets; rules **R80 and R138 retire**, **R98, R137 and R139** now speak of the one
   workflow, and `CiWorkflowTests` holds this shape.

**Consequences.** A pull request costs roughly 25–35 minutes, a docs-only one a few. The `changes` job's
classifier still reports `native`/`maui` for the record. The platform and its downstream apps share this ADR.

**ADR-032 — The platform's tenant is a "household", by design; an app whose tenant is something else renames at rebrand time. (2026-10-06)**
The platform names its tenant with a product noun, not a neutral one: `HouseholdController` at `api/household`,
`HouseholdInvitationsController`, the `Household.razor` page at `/household`, the `Household_*` resource keys
(about 120 strings in EN and ES), the `nav-household` test id, and the word in emails and QA cases. The boundary
audit of 2026-10-05 (Arch A11, #370) asked whether to rename it to a neutral word, make the display noun a per-app
resource, or record it as intended. **Decided: record it as intended.** Both current apps (vuelto, jigger-jot) are
household products, and the one word in code, routes, strings and tests is what lets a journey, a QA case and a
support conversation say the same thing. A neutral word (`tenant`, `workspace`) would be right for nobody and
would still be renamed by every app with a different noun.
*Consequences:* the word is a **rebrand-time** concern, beside the brand itself. An app whose tenant is a team,
a company or a classroom renames `Household` → its noun in the same pass as `Vuelto` → its brand, following
the entry in `docs/REBRANDING.md` §1 (routes, pages, controllers, resource keys, test ids and QA cases move
together; `TestIdContractTests` and `ResourceParityTests` catch a half-done rename). The platform's own tests and
docs keep saying household. Ports of platform changes into an app that renamed are three-way merges with a second
token pair, the same mechanism the brand rename already uses. ADR-C1/C2 (tenant ≠ user; tenant-scoped data) are
untouched: this is vocabulary, not shape.
*Amendment (ADR-034, 2026-10-13):* once the API ships as packages, its routes and controllers keep "household" in
every app; an app with another tenant noun renames the seeded UI (strings, pages, test ids, QA cases) only.

**ADR-033 — Gates guard behaviour and truth, not process. (2026-10-12)**
By the end of the v4 audit, about a third of the platform's gate layer checked the repo's own process instead of
the product: a manifest that named a test for every `[machine]` rule, a gate that held the manifest to the rules
file, a gate that the rule ids cited in tests were final, the PR template's checkboxes, the CLAUDE.md doc map and
its course-reconcile bullet, the add-a-slice checklist against the gates, the wording of deploy sentences, and a
two-way test-id contract with an analyzer for parameterised components. Each was added after a real slip, and
each slip was real; but the gates cost 20–35 billed minutes a PR in upkeep, failed on edits that changed nothing a
user or a reader relies on, and every one of them had to be ported to each app. Before the platform is packaged
(the *Platform packaging* project, milestone M1 "Scissors", #389–#393), the layer is cut to what guards a
behaviour or a document's truth. **Decided:**

1. **Retired** (Pkg 1.1, #389): `RuleIds_CitedInTests_AreFinalRules`, `EveryMachineRule_NamesAStandingCheck` and the
   manifest it read (`tests/Api.Tests/RulesEnforcement.cs` + the app's `RulesEnforcement.App.cs`),
   `PrTemplate_CarriesTheRuleCheckboxes`, `ClaudeMdDocMap_ListsEveryDoc`, `ClaudeMd_CarriesTheCourseReconcileRule`,
   `AddASliceChecklist_NamesEveryArtifactAGateForces`, both `DeployTriggerWording_*`, and
   `TheTwoGatesThatCatchDocsMistakes_AreNeverCodeGated`. The tests the manifest pointed at stay, as ordinary named
   tests; the PR template is a three-line checklist.
2. **Kept, in one place** (Pkg 1.2, #390): every fact that compares a document with the code lives in
   `DocsAreTrueTests` — entities in DATA_MODEL, config keys and settings sections in `.env.example`, compiled-in
   limits, ARCHITECTURE and FLOWS naming real classes and error codes, the Postman descriptions naming each action's
   error codes and gates, the QA case count, and the course gate's wiring. Postman parity, the QA PDFs and sign-off
   sheet (`check_qa_artifacts.py`) and every behaviour, tenancy, RLS, schema, route, slice, ownership, pin, release
   and CI-shape gate are unchanged.
3. **The test-id contract runs one way** (Pkg 1.3, #391): an id a test or a QA case references must exist; an id
   nothing references is not a failure, and the parameterised-component analyzer is gone (an id derived from a
   caller's literal `TestId="x"` counts as existing).
4. **The course check runs when it can find something** (Pkg 1.4, #392): when the course, a file a lesson quotes,
   or the set of tracked files changed (the classifier's `course` output), failing open on an unknown diff.
5. **FOUNDATION_RULES v3.1:** R116 and R117 retire (numbers never reused); R118 and R158 become `[review]`; R85,
   R144, R149, R114 and R115 name their new mechanism; every number is kept for citation, and no gate parses the
   file. The course-reconcile rule stays, as text, in CLAUDE.md's "Read before you act" and in the rules' coda.

**The habit from here on:** a slip gets a test for the thing that slipped (the document that drifted, the code
that misbehaved), never a rule about the rule or a gate on the process that let it through.
*Consequences:* a doc map or checklist can drift without a red build, and is fixed when found; the course is
reconciled once per milestone during packaging, not per commit. The apps take the same cut in their scissors port
(jigger-jot and vuelto P.1), deleting their `RulesEnforcement.App.cs`.

**ADR-034 — The platform ships as packages; a new app starts from a starter (supersedes ADR-019's downstream convention; adopts Backlog §10). (2026-10-13)**
Apps have been clones of the platform, rebranded once and kept in step by porting every fix (ADR-019, its A2
amendment: the ownership map, the stamp, the port tool). The ports work, and they measure the cost: before the
2026-10-08 ports vuelto and jigger-jot differed from the platform in 142 and 144 platform files, after them in 4 and 2,
none of them source — the copies were the same code carried twice. Every fix is a port per app, the cost grows with
apps × fixes, and a missed port drifts silently. Delphos and further apps are coming. **Decided:**

1. **Packages split by runtime target, not by domain.** `Perezosoft.Platform.Core`, `.Infrastructure` and `.Api`
   (the server); `.Client` (the non-visual half of today's Shared.Ui: `AuthService`, the bearer handlers, the session
   and OAuth seams, the persistences, the boot scripts), `.Web` and `.Maui` (the hosts' platform halves:
   `AddPlatformWeb`, `UsePlatformMaui` / `UsePlatformShell`); `.Testing` (the harness, the architecture guards and the
   inheritable gate suites); `.Build` (the solution's build props as `buildTransitive`). Beside them: a dotnet tool
   (`dotnet platform <command>`: the port, the QA generators with their Python inside it), a reusable CI workflow
   (`workflow_call`, the same billed minutes as ADR-031's shape) and `argamboad/.github` for the community files.
   The domains — auth, tenancy, billing, jobs, files, GDPR, MFA, notifications, admin, public API, webhooks,
   observability — are **namespaces inside those packages and options on `AddPlatform(o => …)`**, never packages of
   their own: no app wants half the platform, and a package per domain multiplies versions for no consumer.
   Platform types keep their `Vuelto.*` namespaces in every app; an app declares none (an inherited guard holds
   it). vuelto and jigger-jot renamed `Vuelto.*` to their brand, so their conversion renames platform namespaces
   back in app code, mechanically, by the stamp's brand map.
2. **Three kinds of code, and the rule that decides them: "if a fix here must reach existing apps, it is
   packaged."** **Referenced** — everything that rule catches, in the packages above. **Seed** — the UI (pages,
   components, layout, CSS, strings, brand assets, the UI and browser tests that exercise them) is cloned once from
   the starter and is then the app's, never synced either way: the platform seeds a UI, it never owns one. The
   seeded UI stays as the runnable catalogue of the platform's capabilities, with a capabilities page on Home.
   **Register** — the small set of files every app must hold and keep in step (the hosts' `Program.cs`, `MauiProgram`,
   the two `index.html`, `AppComposition.cs`, `AppDbContext.App.cs`, project files, `global.json`, the CI caller,
   the gates file, the stamp, `render.yaml`, the doc skeletons): cloned, pinned to a platform version, synced by
   recipe.
3. **The register's sync mechanism is the port tool**, scoped to the register: as each package replaces code, its
   files leave the tool's scope and the ownership map's `platform` class shrinks to the register. The tool moves into
   `dotnet platform port`; the manifest gate stays its check.
4. **An app changes platform behaviour by extending it, in this order:** options → contribute an implementation →
   replace a service through DI → decorate it → inherit from it. When none fits, the missing seam goes upstream as a
   new platform seam (plans, permissions, the email brand, routes and policies are the first four). Never a local
   modification of platform code.
5. **Migrations: one context, the app owns every migration.** The app's `AppDbContext` derives from
   `PlatformDbContext`. A platform model change ships in a package release; the app bumps the package and runs
   `dotnet ef migrations add`, and EF emits only the platform's delta into the app's own linear history.
   `PlatformSchemaTests` compares the migrated database with the `platform-schema.json` the package embeds (a bump
   that was not migrated fails the app's PR), and the RLS migration gate covers any new policy. Platform model
   changes are **additive** (expand; a contraction ships in a later release, after every app expanded); data steps
   and RLS policies ship as callable steps (`ApplyPlatformStep(name)`, like `RlsDdl`) that the release note names.
   *Rejected:* a separate platform DbContext with its own history table — no cross-context navigations, and two
   histories per database.
6. **"Household" stays the internal API noun** (amends ADR-032's consequence): the packaged controllers and routes
   (`api/household`, `HouseholdController`, `HouseholdInvitationsController`) keep it in every app; an app whose tenant
   is something else renames the seed — strings, pages, test ids, QA cases — and never the packaged API.
7. **Release discipline.** Semantic versions from git tags through **MinVer**; prereleases on the way to 1.0
   (`1.0.0-alpha.1` backend, `-alpha.2` client, `-alpha.3` tooling, `-beta.1` for the pilot). Packages publish to
   **GitHub Packages** (the NuGet feed under `argamboad`), read with a token in every environment (laptop, codespace,
   cloud session, CI), and only from the manual release workflow (ADR-031: no push trigger, no schedule). Every
   release note says three things: **schema change** (yes/no; the steps to apply), **register change** (the files and
   the recipe) and **breaking** (yes/no; what to do). On the laptop, `PlatformSource=<path>` turns the package
   references into project references to a platform checkout, for work on platform and app together.

*Consequences:* a fix reaches every app as a package bump; an app holds its own code, its seeded UI and about twenty
register files, and runs no platform suites — only the inherited guards against its own code, a few composition
smokes, its own tests and the browser journeys it keeps. ADR-019's identity (the `Vuelto.*` names, the frozen
DataProtection purposes) stands; its downstream convention and Backlog §10's "defer hard" are superseded. The work
is the *Platform packaging* project (platform milestones M2–M7): jigger-jot converts on the beta as the pilot (a
clone restructured to references, compared side by side with its current tree), vuelto on 1.0, and new apps start
from the starter (`dotnet new`). Until an app converts, it stays a clone kept in step by the port tool.

*Amendment (2026-10-13, Pkg 2.5, #430) — what the publisher keeps, and what the brand map never renames.* The first
port of M2 into jigger-jot (jigger-jot#192) showed two holes. (1) The release machinery — `release.yml`, the note
template, `tools/release/**` (the dry-run package), its ci-logic cases and `platform-packages.props` — only makes sense
in the publisher, yet the port forced it on the app, and removing it failed five gates. It is now a fifth ownership
class, **`producer`**: the port tool never writes it downstream (an old copy is reported for removal), the manifest
leaves it out, and the gates that read it run only in the platform (`PlatformOwnership.IsThePlatform`: the stamp's
commit is null); `ProducerFiles_ExistInThePlatform_AndNowhereElseAreDemanded` keeps them demanded here. (2) The brand map
renamed the platform's package ids (`Perezosoft.Platform.*` → `JiggerJot.Platform.*`) and the feed key — so, once an app
referenced a platform package, its source mapping would have missed it (sending it to nuget.org's `*`: a
dependency-confusion opening) and the source switch would never fire. `platform-ownership.json` now carries a
**`keep`** list (`Perezosoft.Platform.`, `key="perezosoft"`) that the port tool reads from the platform commit it ports
to and never rewrites; `PlatformOwnership.LocalPath` spells paths with the same list. The gate hashes the app's own text,
where a kept token is already the platform's, so the list changes no hash — the tool and the gate agree by construction,
and the port-tool test proves it on a real port. The ci-logic course cases now declare `requires` the course and skip
in an app; `no-course-here` runs everywhere.

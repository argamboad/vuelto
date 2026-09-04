# Epic `EMAIL` — Email ingestion (bank vouchers → drafts)

> Registered epic key: **EMAIL**. Port slices **P9** (connections, consent & readers) and **P10**
> (staging, suggestions & review queue) (ADR-V001), re-homed from donor stories **US-025–US-030**
> plus the hardening waves **US-033–US-038**, **WU-3**, **WU-5** — `vuelto-legacy/docs/stories/` and
> `docs/design/email-ingestion-design.md` (decisions D1–D6). Decision context: ADR-V002 (user-scoped
> mail connections), ADR-V010 (vouchers stage as inert drafts; confirm is the only draft → transaction
> path), `FEATURES.md` §18–20. Stories land one at a time; each PR closes one.

### EMAIL-1 — Parse a bank voucher email into a draft (parser library) ✅

**As the** ingestion pipeline
**I want** a pure, best-effort parser that turns a BAC or BN voucher email into a structured draft and
says which required fields it could not read
**So that** a misparse is a fixable suggestion in the review queue, never a corrupted transaction

**Context / notes:** pure code, no entity, no route, no UI. **Core** (`Vuelto.Core.Vouchers`): the
contracts (`VoucherMessage`, `ParsedVoucher` with `MissingFields` / `IsComplete`, `VoucherBank`,
`IBankVoucherExtractor`, `IVoucherParser`), the tolerant helpers (`SpanishDateParser` — Spanish month
names incl. the CR "set", the banks' month-first shape, numeric dates always day-first;
`VoucherText` — accent/colon/case-insensitive label normalization, money parsing where a lone dot with
three trailing digits is a thousands separator, currency words, card brands), and **routing as data**
(`BankVoucherMap` of `VoucherRoutingRule`s — sender substring and/or subject prefix, first match wins,
a rule with neither never matches; the `Default` map is subject-only; `KnownVoucherSenders` carries the
verified From-addresses for the fetch filters). **Infrastructure** (`Vuelto.Infrastructure.Vouchers`,
HtmlAgilityPack stays here): three extractors that **never throw** — `BacVoucherExtractor`
("Notificación de transacción": label/value rows), `BnVoucherExtractor` ("Voucher Digital": styled
banner + table, single-cell date), `BnPaymentExtractor` ("BN Conectividad le informa": styled `<font>`
labels with the value as the next text node; the paid-service name is the first heading's first line,
never the line-items header) — and the `VoucherParser` facade that routes, contains a throwing
extractor, and validates merchant / amount > 0 / currency ∈ {CRC, USD} / date into `MissingFields`.
`AddVoucherParsing()` registers all of it. The three real anonymized bodies are kept as fixtures so a
silent bank format change shows up in CI first. Data-driven bank definitions (donor Slice 7) remain a
later roadmap item: adding a bank is still a class here.

```gherkin
Scenario: A BAC voucher parses completely
  Given a "Notificación de transacción" email whose table has Comercio, Fecha "12 Ene, 2026 - 14:30", VISA, Autorización, Referencia, Tipo de Transacción, Monto "CRC 52000.00"
  When the parser handles it
  Then bank Bac, merchant SUPER MARIANO, date 2026-01-12, card ************1234, auth, reference, type COMPRA, CRC 52000.00, IsComplete

Scenario: Both BN formats parse
  Given a "Voucher Digital" email → merchant from the green banner, type from "comprobante de COMPRA", the single-cell date, NRO AUT / REF / TOTAL rows, MASTERCARD card
  Given a "BN Conectividad le informa" email → merchant = the first bold heading's first line, No. comprobante débito as reference AND authorization, Moneda COLONES → CRC, Monto, Tarjeta, "16/06/2026 14:30:00" → 2026-06-16, type PAGO

Scenario: Routing is data
  Given the default map
  Then "Notificación de transacción - SUPER MARIANO" → bac; "notificación de transacción" → bac (case-insensitive); "Voucher Digital" → bn-voucher; "BN Conectividad le informa" → bn-payment; "Your monthly statement" → nothing
  Given a custom map with a sender rule "baccredomatic.com" and a subject rule "Compra aprobada" both → bac
  Then either matches; a rule with both fields needs both; a rule with none never matches; the first matching rule wins

Scenario: Best effort, never a crash
  Given broken HTML, an empty body, or an extractor that throws
  Then the result is a bank-only voucher with every required field listed in MissingFields (a zero amount counts as missing)
  And an email no rule matches yields null (skipped, not an error)

Scenario: Tolerant text
  Then "Comercio:", " comercio ", "Autorización:" and "Nro. Aut:" normalize to COMERCIO / AUTORIZACION / NRO AUT
  And "₡52,000.00" → CRC 52000; "$ 1,234.50" → USD 1234.5; "5.000,75" → 5000.75; "₡1.500" → 1500 (not 1.5); "1.234.567" → 1234567
  And "07 Set 2026" → 2026-09-07; "Ene 13, 2026, 14:01" → 2026-01-13; "06/07/2026" → 6 July (day-first)
```

**Out of scope:** reading mailboxes (EMAIL-2/3), staging and dedup (EMAIL-4), the review queue
(EMAIL-6), data-driven bank definitions (roadmap).

**Definition of done:** tests first; Core.Tests (`SpanishDateParserTests`, `VoucherTextTests`,
`BankVoucherMapTests`); Api.Tests (`VoucherExtractorTests`, `VoucherParserTests` incl. the throwing
extractor and the DI wiring, `RealVoucherFixtureTests` over the three captured bodies); HtmlAgilityPack
pinned centrally (MIT, license scan green); `AddVoucherParsing()` registered; merged.

### EMAIL-2 — Connect an inbox (consent, filters, read-only) ✅

**As a** household member who receives bank vouchers by email
**I want** to connect my Outlook or Gmail inbox with read-only access and say which folders and senders/subjects carry vouchers
**So that** the app can fetch only those emails, on my account, without ever touching the mailbox

**Context / notes:** `EmailConnection` is **user-keyed, not tenant-scoped** (ADR-V002): it survives leaving
or dissolving a household and is wiped with the account through `EmailConnectionUserDataContributor`.
A connection is created **only** through the consent round-trip — `GET /api/email/connections/authorize?
provider=` returns the provider URL (read-only scope `Mail.Read` / `gmail.readonly` plus `openid email`
for the account address and `offline_access` / `access_type=offline` for a refresh token, biased to the
signed-in account with `login_hint`), the IdP redirects to `GET /api/email/connections/callback`, which
exchanges the code, stores the tokens **protected by the platform Data Protection key ring** (ADR-V016 —
no separate secret) and sends the browser back to `/email?connected=…` or `?email_error=…`. The callback
is the one **anonymous** route in the group: the signed, 15-minute Data-Protection **state** is its
authorization (the same idea as `/api/files/{token}`). The consent apps are the platform's own
`Authentication:Microsoft` / `Authentication:Google` credentials — nothing new to provision; an
unconfigured provider answers 400 `provider_not_configured`. `POST /api/email/connections` is refused
(`use_consent_flow`) so tokens never arrive in a client body. Defaults: unread-only, `import_from` = now,
cursor = `import_from`, 15-minute interval, the verified BAC/BN senders + subject prefixes pre-seeded.
Rules: one inbox per provider per user (`connection_exists`), at least one sender or subject filter,
folders travel as `{id, name}` pairs (the name is captured at pick time so the page can say what is
scanned without a provider round-trip, back-filled once from the provider for rows that predate it, `null`
when unresolvable — never the id; readers use the id),
interval 5…1440 minutes, lowering `import_from` pulls the cursor back (backfill) while raising it never
advances the cursor (that would silently skip un-imported mail). Tokens are never returned; another
user's id is a uniform 404.

```gherkin
Scenario: Connect an inbox
  Given I am signed in and Authentication:Google is configured
  When I GET /api/email/connections/authorize?provider=google
  Then the URL targets accounts.google.com with client_id, response_type=code, the read-only scope, openid email, access_type=offline, prompt=consent, my email as login_hint and a signed state
  When Google redirects to the callback with a code and that state
  Then a connection exists for me with protected tokens, account_email from the id_token, unread_only true, import_from = now, cursor = import_from, interval 15, BAC/BN senders and subjects pre-filled
  And the browser lands on /email?connected=google
  When the callback arrives with a tampered, expired or missing state, or with error=access_denied
  Then nothing is created and the browser lands on /email?email_error=consent_failed
  And once a provider is connected its Connect button is disabled (one inbox per provider); a second consent for it by hand → /email?email_error=already_connected

Scenario: Only the consent flow creates connections
  When I POST /api/email/connections with tokens → 400 use_consent_flow
  When I GET /authorize?provider=outlook → 400 invalid_provider; for a provider without credentials → 400 provider_not_configured

Scenario: Edit, list, disconnect — mine only
  When I PUT folders, sender/subject filters (comma list), unread_only, ignore_cursor, import_from, polling_interval_minutes
  Then the connection changes; blank filters → 400 filters_required; interval 4 or 2000 → 400 invalid_interval
  When I lower import_from by 7 days → last_polled_at follows; when I then raise it by 4 days → last_polled_at stays
  When another user PUTs, GETs or DELETEs my connection → 404
  When I DELETE → 204, ingestion stops, imported transactions stay

Scenario: The Email settings page
  Given Settings → "Manage inboxes"
  Then each inbox shows provider, account, Active / Needs reconnect, last checked and interval; Reconnect appears on a dead one
  When I click Connect Gmail → the browser goes to the consent URL; on an unconfigured provider a clear message
  When I Edit → Load folders lists the account's folders as checkboxes (Graph subfolders as Inbox/Vouchers); Save PUTs the settings
  When I Disconnect → a confirm step, then the inbox is gone
```

### EMAIL-3 — Provider readers (Graph + Gmail) ✅

**As the** ingestion pipeline
**I want** one reader per provider that fetches only filter-matching voucher mail and hands back parser-ready messages
**So that** staging (EMAIL-4) is provider-independent and the mailbox is never modified

**Context / notes:** `IEmailReader` with `GraphEmailReader` and `GmailEmailReader` over a shared
`OAuthEmailReader`: unprotect the access token, run the provider query, on 401 refresh **once** through
`IMailConsentService.RefreshAsync` (persisting the new tokens; a provider that omits a new refresh token
keeps the old one) and retry; a still-unauthorized connection is flagged `needs_reconsent` and skipped —
never thrown out of the loop; 429/5xx skip this poll without advancing the cursor; any other HTTP failure
flags the connection. **Every filter is pushed into the provider query** (`EmailQuery` → Graph `$filter`
with `receivedDateTime ge`, `isRead eq false`, exact sender OR `startswith(subject)`; Gmail `q` with
`label:`, `from:(…)`, `subject:(…)`, `after:`, `is:unread`) with a 5-minute cursor look-back (dedup covers
the overlap); `ignore_cursor` drops the date floor. Graph pages `@odata.nextLink` (only while it stays on
graph.microsoft.com), Gmail pages `nextPageToken`; both cap at 20 pages and report `Saturated` so the cursor
never jumps past unfetched mail; a failing folder or message is skipped, not fatal. Graph folders recurse
`childFolders` with `Inbox/Vouchers` path names; Gmail lists labels. **GET only** — read-only by construction
(ADR-V010). Outbound hosts are fixed provider hosts (R76 allowlist with rationale).

```gherkin
Scenario: Fetch pushes the filter and maps the payload
  Given a Microsoft connection with folder Inbox, a BAC sender and subject prefix, unread-only
  When the reader fetches
  Then the single request is a GET on /me/mailFolders/Inbox/messages whose $filter carries receivedDateTime ge, isRead eq false and startswith(subject
  And the message maps id, subject, sender address, receivedDateTime and the HTML body

Scenario: Refresh once on 401
  Given the first call answers 401 and the refresh returns new tokens
  Then the call is retried, the new tokens are stored protected, the connection stays active
  Given the refresh fails → the connection is flagged needs_reconsent, no messages, no further calls
  Given a 429 → no messages, no flag (retry next cycle)

Scenario: Resilience and paging
  Given folders BadFolder (400) and Inbox (one voucher) → the voucher is returned
  Given two Graph pages via nextLink, the second pointing off-host → both pages' messages, oldest first, Saturated
  Given Gmail returns ids over two pages → both messages are fetched with pageToken, HTML part base64url-decoded

Scenario: Folder pickers
  Then Graph lists Inbox and Inbox/Vouchers (childFolders); Gmail lists labels; a 401 refreshes like fetch
```

**Definition of done (EMAIL-2/3):** tests first; Core.Tests (`EmailQueryTests`); Api.Tests
(`MailQueryBuilderTests`, `MailConsentServiceTests` incl. the Data-Protection state, `EmailReaderTests` on
Postgres for the persisted refresh/flag side effects, `EmailConnectionSliceTests` incl. the erasure
contributor, HTTP `EmailEndpointTests` incl. the anonymous callback redirects); bUnit
(`EmailSettingsPageTests`); migration `AddEmailConnections` (user-keyed — no RLS policy by design);
`IUserDataContributor` + arch-test `handled` entry; R76 allowlist entries; Postman folder; QA-EMAIL-01..03 +
regenerated PDFs; EN/ES resx; Settings card; ADR-V016; merged. **Live consent against Microsoft/Google is
the IdP boundary — verified manually (QA-EMAIL-02).**

### EMAIL-4 — Staging pipeline & dedup ✅

**As the** household
**I want** voucher emails turned into inert review drafts automatically, without duplicates and without touching the mailbox
**So that** nothing lands in the budget until someone confirms it

**Context / notes:** two household-scoped tables (RLS): `PendingVoucher` (the parsed draft + resolved bank +
room for a suggestion, `pending` → `confirmed` | `discarded`) and `IngestedVoucher` (the dedup tombstone,
unique on `(TenantId, Fingerprint)`). `VoucherFingerprint` = SHA-256 of bank | authorization ?? reference |
amount | date; both ids blank → the provider message id; nothing at all → null (stage anyway — under-dedup
is recoverable). **The tenant hop:** a connection is user-keyed, so `VoucherStagingService` resolves the
owner's *current* household and enters that tenant before staging — the stamping interceptor and RLS then
scope every draft (ADR-V002/V010). Bank resolved by name (`BAC Credomatic`, `Banco Nacional`) with a
Cash / Efectivo fallback; a household with no banks gets the defaults seeded in the owner's locale first (a
concurrent seed is absorbed). Unrecognized mail is skipped. **Cursor rules:** held at the oldest transient
failure so it retries next poll (dedup covers the re-fetch), poison mail older than 7 days is dropped and
never stalls, a saturated page resumes from the newest fetched message minus the overlap, otherwise the
cursor advances to the poll start; a reconsent result stages nothing and leaves the cursor alone. A failed
save is detached so the next message is unaffected. **The poller** is `EmailPollJob`, an `IScheduledJob`
on the platform scheduler (1-minute interval) that stages each active connection due by its own interval,
isolating a throwing one. **"Sync now"** (`POST /api/email/connections/{id}/sync`) runs the same staging
on demand → `{ staged, duplicates, unrecognized }` or 409 `needs_reconsent`. Both tables are wiped on
dissolve and drafts exported (`pending_vouchers`). Suggestions stay blank until EMAIL-5; the review queue is
EMAIL-6.

```gherkin
Scenario: A voucher email becomes an inert draft in the owner's household
  Given a Microsoft connection owned by a member of household H, and one BAC voucher email
  When the poller (or Sync now) runs with no ambient tenant
  Then a pending draft exists in H with the parsed bank/merchant/amount/currency/date/auth/reference, the resolved bank and no suggestion
  And a tombstone exists with the same fingerprint; no month and no transaction exist

Scenario: Dedup per household, and the tombstone outlives the draft
  When the same unread email is fetched again → duplicates 1, still one draft
  When the draft is discarded and the email is fetched again → still not re-staged
  When another household stages the same fingerprint → it stages there

Scenario: Bank resolution
  Then a BAC voucher maps to "BAC Credomatic" when the household has it, else to Cash
  Given a household with no banks and a Spanish-locale owner → Efectivo + the CR banks are seeded first and the draft resolves BAC

Scenario: Cursor rules
  Given m1 (2h ago) succeeds and m2 (1h ago) fails transiently → last_polled_at = m2's received time; the retry stages m2 and dedups m1
  Given the reader saturated its page cap with the newest message at 1h ago → last_polled_at = that time − 5 min, never "now"
  Given a message older than 7 days keeps failing → it is dropped and the cursor advances
  Given needs_reconsent → nothing staged, cursor unchanged

Scenario: The poller
  Given connections due (last polled 20 min ago at a 15-min interval), never polled (due from import_from), not due, and needs_reconsent
  When the job runs → only the due and never-polled ones stage; a throwing connection is logged and the others still run; cancellation propagates

Scenario: Sync now
  When I click Sync now on my inbox → "Sync done — N staged for review, M already seen, K not a voucher."
  When the inbox needs reconnecting → "Reconnect this inbox to sync it." (409)
```

### EMAIL-5 — Merchant → category suggestions ✅

**As a** household member
**I want** rules that say "vouchers from this merchant belong to this category (and class)"
**So that** a staged voucher arrives in the review queue with the right category already picked — and I
still confirm it

**Context / notes:** port of donor US-029 (D4) + WU-5 B12. Household-scoped `MerchantCategoryMapping`
(`ITenantScoped`, RLS in migration `AddMerchantCategoryMappings`): the pattern as typed plus a stored
lower-cased `PatternKey` with a **unique `(TenantId, PatternKey)` index** — one rule per merchant text per
household regardless of casing; the handler pre-checks (`409 mapping_exists`) and the index catches the
concurrent race (same 409). **Matching is `MerchantMatcher` in Core** — case-insensitive "contains", the
**longest** (most specific) pattern wins, ties by text — shared by staging and the slice so there is one
definition. A rule may carry a `suggested_class` from `SuggestibleClasses` (the three spending classes) or
none (→ `budgeted` at staging). Validation: non-blank pattern ≤ 200, a known class or none, a category
that exists and is active in this household. **Staging (EMAIL-4) loads the household's rules once per
connection and copies the match onto the draft** (`SuggestedCategoryId` + `SuggestedClass`) — never
applied; unmapped merchants stay blank and still stage. Plain delete (the suggestion was copied, not
FK'd). `MerchantMappingDataContributor` (`merchant_mappings`) wipes on dissolve and exports. Routes
`/api/merchant-mappings` (list with category names — an inactive category still names its rule; create
201; update; delete 204; uniform 404). UI: **Settings → Manage suggestions** (`/merchant-mappings`):
add / edit / two-step delete. **Learn-on-confirm** (EMAIL-6) is this slice's create through
`RememberAsync` — an existing rule is never overwritten.

```gherkin
Scenario: The longest matching rule suggests, and only suggests
  Given rules "TACO" → Groceries (no class) and "Taco Bell" → Dining (extraordinary)
  When a voucher from "TACO BELL PLAZA REAL C" is staged → suggested Dining / extraordinary
  When a voucher from "SUPER TACO SAN JOSE" is staged → suggested Groceries / budgeted (the default class)
  When a voucher from "WALMART" is staged → no suggestion, still staged
  And no draft is ever confirmed by a rule

Scenario: One rule per merchant text per household
  Given a rule "AutoMercado"
  When I create "AUTOMERCADO " → 409 mapping_exists; when two households race the same text → exactly one rule, the loser gets 409
  When I rename another rule to "AUTOMERCADO" → 409; renaming the same rule to "AUTO MERCADO" → allowed
  And another household never sees the rule (uniform 404), nor can it point a rule at my category (400)

Scenario: Validation
  Then a blank pattern, an unknown class ("inflow"), a missing/inactive/foreign category → 400 invalid_request; " Extraordinary " normalizes to extraordinary

Scenario: Manage suggestions page
  When I add "AUTOMERCADO" → Groceries → "Rule saved." and it lists with the category name
  When I add it again → "A rule for this merchant already exists."
  When I edit its class and delete it (two-step) → the list follows
```

**Out of scope:** learning without an explicit opt-in, ML/fuzzy matching, rules shared across households.

**Definition of done:** tests first; Core.Tests (`MerchantMatcherTests`); Api.Tests
(`MerchantMappingSliceTests` on Postgres incl. the race, the suggestion case in `VoucherStagingSliceTests`,
`ReviewEndpointTests` over HTTP); Ui.Tests (`MerchantMappingsPageTests`); RLS gate green; arch `handled`
entry; Postman folder 22; QA-EMAIL-05; merged.

### EMAIL-6 — Review queue & confirm ✅

**As a** household member
**I want** to see the vouchers staged from my inboxes, pick the category and class, and confirm or discard
each one
**So that** a bank voucher becomes a real transaction only when I say so — through the same rules as a
manual entry

**Context / notes:** each card's category picker is the shared `CategoryPicker` — a category can be created
right on the card (**+ New**) and every other card on the queue lists it at once. port of donor US-030 / US-033 / US-038 (as reversed by the owner: only category and
class are edited in the queue) + WU-3 A6. **Confirm is the only draft → transaction path** (ADR-V010).
Feature slices may not reference each other (R7), so the Ledger's `TransactionHandler` implements a new
**Core contract `ITransactionService`** (`CreateTransactionCommand` carries the provenance `Source`);
`Program.cs` binds it, and `PendingVoucherHandler` depends on the contract. Confirm: the draft must be
`pending` (else `409 not_pending`); `category_id` + `transaction_class` (one of the three spending classes)
are the user's decision; the command is the voucher's own data (payee = merchant, bank, amount, currency,
date) with optional overrides — the UI opens a field **only where the parser left a blank**
(`missing_fields`) — booked with `source = email` and no manual rate (the live rate resolves and freezes,
like manual entry). **One boundary**: `IUnitOfWork.BeginTransactionAsync` → create → conditional
`ExecuteUpdate pending → confirmed` (+ `ConfirmedTransactionId`) → commit. A ledger validation or
`exchange_rate_unavailable` failure writes nothing and the draft stays pending; a second concurrent confirm
loses the flip (0 rows), returns `not_pending`, and its scope disposes without commit — **the transaction
and any month it created roll back**, so exactly one transaction ever exists. **Discard** is the same
guarded flip to `discarded` (0 rows → 409 if the draft exists, else uniform 404), so it can never revert a
draft a concurrent confirm just committed. The dedup tombstone is untouched by both — a re-fetched email
never re-stages. `remember_merchant` runs **after** the commit through EMAIL-5's `RememberAsync`
(non-critical, never overwrites). A booked `email` transaction is the user's own data: it is **editable and
deletable** like a manual one (`TransactionSources.IsEditable`); only refund-derived inflows stay read-only
— the walkthrough caught the month list labelling an email row "Derived from a refund". Routes `/api/pending-vouchers` (list pending newest mail first, `/count`,
`/{id}/confirm`, `/{id}/discard`). UI: **Review** page (`/review`, nav item with a **count badge** that
re-counts on every navigation), suggestion-prefilled category/class, parsed fields read-only, the blanks
editable, remember-merchant opt-in, confirm/discard, 409 → "already handled — reloading"; **dashboard
banner** "N voucher(s) waiting for review → Review now" (shown even before the first month exists).

```gherkin
Scenario: Confirm books the transaction through the ordinary create
  Given a pending BAC draft TACO BELL ₡7,620 on 2026-06-13 resolved to BAC Credomatic, and a live rate of 500
  When I confirm it as Dining / extraordinary
  Then a transaction exists with source email, that payee/bank/amount/currency/date, ₡7,620 / $15.24, rate 500 frozen, credit_card
  And June 2026 was auto-created for it; the draft is confirmed with the transaction id; the tombstone remains; the count is 0

Scenario: Nothing partial, ever
  When the category or class is missing/invalid, or the amount is blank (0), or no rate resolves
  Then 400 (invalid_request / exchange_rate_unavailable), no transaction, no month, the draft stays pending

Scenario: Exactly one transaction under a concurrent confirm
  When two requests confirm the same draft at once
  Then one succeeds; the other gets 409 not_pending and its transaction (and month) rolled back; the draft names the winner

Scenario: Discard is a guarded flip
  When I discard a pending draft → 204, status discarded, tombstone stays; confirming it → 409
  When I discard a draft that was just confirmed → 409 not_pending and it stays confirmed
  When the id is unknown or another household's → 404

Scenario: The blanks are editable, the rest is not
  Given a BN draft whose merchant, amount and currency could not be read
  Then the queue shows "Could not read: Merchant, Amount, Currency" and opens payee, amount and currency; the date stays read-only
  When I fill them and confirm → the overrides are sent; a parsed draft sends none

Scenario: Learn on confirm
  When I confirm with "Remember this merchant" → a rule merchant → category (+ class) is created; a second confirm of the same merchant never overwrites it

Scenario: Badge and banner
  Given 3 pending drafts → the header's Review link shows 3 and the dashboard banner says 3 are waiting (even with no months); at 0 or when the count can't be read, both are hidden
```

**Out of scope:** editing parsed fields the parser did read (owner decision — fix the mapping or discard and
enter manually), bulk confirm, an audit of who confirmed what (Slice-8 candidate).

**Definition of done:** tests first; Api.Tests (`PendingVoucherSliceTests` on Postgres incl. the
two-context concurrency proof and the nothing-partial cases; `ReviewEndpointTests` over HTTP with a seeded
draft and the rate resolved through the chain's last tier); Ui.Tests (`ReviewPageTests`, `ReviewBadgeTests`);
`ITransactionService` bound in `Program.cs` (R7/R8 gates green); Postman folder 22; QA-EMAIL-06; merged.

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
  When I connect the same provider twice → /email?email_error=already_connected

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

### EMAIL-4 — Staging pipeline & dedup 🔲
### EMAIL-5 — Merchant → category suggestions 🔲
### EMAIL-6 — Review queue & confirm 🔲

*(EMAIL-4..6 are authored as they land — P10.)*

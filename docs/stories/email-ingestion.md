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

### EMAIL-2 — Connect an inbox (consent, filters, read-only) 🔲
### EMAIL-3 — Provider readers (Graph + Gmail) 🔲
### EMAIL-4 — Staging pipeline & dedup 🔲
### EMAIL-5 — Merchant → category suggestions 🔲
### EMAIL-6 — Review queue & confirm 🔲

*(Authored as each lands — P9b and P10.)*

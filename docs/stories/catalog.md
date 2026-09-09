# Epic `CATALOG` — Categories & banks

> Registered epic key: **CATALOG**. Port slice **P2** (ADR-V001): the household's two name catalogs,
> re-homed from donor stories **US-010** (categories), **US-013** (banks), **US-019** (seeded starter
> catalog) and **US-047** (language-aware seeding) — `vuelto-legacy/docs/stories/`. Decision
> context: ADR-V008 (soft delete + 409 reactivation offer; uniform 404), ADR-V009 (seed once in the
> caller's locale; no retranslation), ADR-V007 (every transaction names a bank), `FEATURES.md` §12,
> `DATA_MODEL.md` → `Category`, `Bank`.

Both catalogs share one shape (`ICatalogEntry`) and one behaviour (`CatalogHandler<T>`): the stories
below differ only in seed data and error-code prefix.

### CATALOG-1 — Keep the household's categories

**As a** household member
**I want** to add, rename, deactivate and reactivate the categories we classify money by
**So that** transactions and budget lines always land in a bucket we recognise, and old buckets
never vanish from history

**Context / notes:** names are unique per household, **case-insensitively**; there is no delete —
`is_active: false` hides an entry from pickers but keeps it on past rows. The first read for a
household seeds the 7 example categories from `SeedCatalog` in the reader's locale (JWT `locale`
claim; English base), once; a later language switch does not retranslate (D4). Any member may edit.
Another household's id is **not found** (404), never 403.

**Acceptance criteria**

```gherkin
Scenario: A new household's first read seeds the defaults in the reader's language
  Given my household has no categories yet and my locale is "es"
  When I GET /api/categories
  Then I receive the 7 example categories in Spanish ("Alimentación" … "Otro"), all active
  And a second read (in any locale) returns the same 7 — nothing is seeded twice

Scenario: Creating a category
  When I POST /api/categories with name "  Viajes "
  Then I receive 201 with name "Viajes" (trimmed) and is_active true

Scenario: A name clash is a 409, case-insensitively
  Given a category "Viajes" exists and is active
  When I POST /api/categories with name "VIAJES"
  Then I receive 409 with error "category_exists" and no existing_id

Scenario: An inactive clash offers reactivation
  Given a category "Gym" exists and is inactive
  When I POST /api/categories with name "gym"
  Then I receive 409 with error "category_exists_inactive", existing_id = that category's id and existing_name "Gym"
  When I PUT /api/categories/{existing_id} with name existing_name and is_active true
  Then "Gym" is active again, keeps its stored name (not the typed "gym"), and appears in the active list

Scenario: Rename and deactivate
  When I PUT /api/categories/{id} with a new name and is_active false
  Then GET /api/categories omits it and GET /api/categories?include_inactive=true shows it inactive
  And renaming to another category's name is 409 "category_exists"; renaming to my own name is fine

Scenario: Blank names and foreign ids
  When I POST or PUT with a blank name
  Then I receive 400 "invalid_request"
  When I PUT /api/categories/{id of another household's category}
  Then I receive 404 and their category is unchanged

Scenario: The Categories page
  Given I am signed in
  When I open /categories (linked from Settings → Catalog)
  Then I see every category with an Active/Inactive badge and an Edit button
  When I click New, type a name and Create
  Then it appears in the list; a clash with an inactive name shows a Reactivate button that restores it
  When I click Edit on a row far down the list
  Then the page scrolls so the whole edit card is in view (2026-09-07; same helper as the budget lines)
```

**Out of scope:** the 70-category Excel import (PROJECT_BRIEF OUT list); anything that uses
categories (P5+).

### CATALOG-2 — Keep the household's banks and cash

**As a** household member
**I want** the same for our money sources
**So that** every transaction can name where the money came from (ADR-V007)

**Context / notes:** identical to CATALOG-1 with error codes `bank_exists` /
`bank_exists_inactive`, under `/api/banks` and `/banks`. Seed = **Cash** (`Efectivo` in Spanish —
the only localized bank) + BAC Credomatic, Banco Nacional, BCR, Banco Popular, Scotiabank,
Davivienda, Promerica, Lafise. Cash stays first: it is the fallback for cash spending and for
vouchers whose bank can't be matched (P10).

```gherkin
Scenario: Banks seed with Cash first
  Given my household has no banks yet and my locale is "es-CR"
  When I GET /api/banks
  Then I receive 9 banks including "Efectivo" and "BAC Credomatic"

Scenario: Everything else behaves like categories
  Then the create / clash / reactivate / rename / 404 scenarios of CATALOG-1 hold under /api/banks
```

**Definition of done (both):** tests first; Core.Tests (`SeedCatalog`), Api.Tests slice tests on
Postgres (seeding in locale, idempotence, 409 offer, rename/reactivate, list filter, cross-tenant read
AND write negatives, contributors) + HTTP (401, 201/200, 409 shape, 404, both prefixes), bUnit page
tests; migration with RLS DDL for both tables; two contributors; Postman folder; QA-CAT-01..04 +
regenerated PDFs; EN/ES resx; merged, app working.

### CATALOG-3 (CARDS-1) — Keep the household's cards, named by us, identified by the bank *(owner request, 2026-09-08)* ✅

**As a** household member
**I want** every card we pay with to exist as a thing I can name, and every voucher to land on the right one
**So that** I can later see how much went through each card

**Context / notes (ADR-V021):** a card is identified by what the voucher prints — **brand + last four** —
and named by us (the alias). A confirmed voucher links its card, **creating it as `VISA-1234`** on first
sight (`auto_named` until renamed); the alias is edited on **Settings → Cards** (`/cards`), never the
identity. Manual transactions get an optional card picker; the month page shows and filters by card; the
CSV gains a trailing `card` column. Transactions from before, or without a card, keep `card_id` null —
a "no card" bucket in every summary, no LEGACY row (the summaries themselves — dashboard "By card", Reports "Spend by
card" — are CARDS-2, REPORTS-6, 2026-09-09). Rules under `/api/cards`: alias unique
case-insensitively, `card_exists` (alias **or** identity clash) / `card_exists_inactive` + `existing_id`
+ `existing_name`, uniform 404, never seeded. BN payment receipts name no brand → `CARD-0000`.

```gherkin
Scenario: A voucher creates the card on first sight
  Given the household has no cards and a BAC voucher for VISA ************1234 is confirmed
  Then a card VISA-1234 (VISA, 1234, the voucher's bank, auto_named) exists and the transaction names it
  When a second voucher on the same card is confirmed
  Then it names the same card — one row, not two

Scenario: The household names the card
  When I rename VISA-1234 to "Allan's Visa" on Settings → Cards
  Then every past and future transaction on it shows "Allan's Visa", auto_named is false, and the next voucher still matches by VISA + 1234

Scenario: The voucher prints no brand
  Given VISA-1966 exists and a BN payment for ************1966 (no brand) is confirmed
  Then it lands on VISA-1966 — a brandless number is the card already known by those four digits
  And when CARD-1966 was created first and a VISA voucher for 1966 arrives, CARD-1966 becomes VISA-1966 (still auto-named)

Scenario: The bank renews the card
  Given "Allan's Visa" (VISA ····1234) and a voucher printed as VISA ************5678 is confirmed
  Then VISA-5678 appears auto-named with the new transaction on it
  When I choose "Same card as… Allan's Visa" and Merge
  Then one card remains, listing ····1234 · ····5678, every transaction on it, and the next voucher on either number lands on it

Scenario: Manual entry
  When I enter a transaction without picking a card
  Then it has no card; picking one links it; a card from another household or an inactive one is 400
```

**Definition of done:** `CardIdentityTests`; `CardSliceTests` (normalise, alias/identity 409, reactivate, resolve-or-create,
rename clears auto, tenant isolation, contributor); ledger + voucher-confirm tests; `CardEndpointTests`; bUnit
`CardsPageTests` + form/month/review assertions; migration `AddCards` with RLS; Postman folder 24; QA-CAT-05 +
QA-EMAIL-06 / QA-REP-02 lines; EN/ES resx; snapshot tool; ADR-V021.

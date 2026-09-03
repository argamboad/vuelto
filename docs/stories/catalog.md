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

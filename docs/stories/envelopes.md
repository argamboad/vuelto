# Epic `ENV` — Envelopes

> Registered epic key: **ENV**. Port slice **P4** (ADR-V001): the household's savings envelopes,
> re-homed from donor story **US-012** (the envelope-management half, PRD §4) and donor **ADR-0018**
> (envelopes are transactional; contributions are a transaction class) — `vuelto-legacy/docs/stories/`.
> Decision context: ADR-V007 (annual target + reminder cadence, no static contribution), ADR-V008
> (soft delete, 409 reactivation offer, uniform 404), `FEATURES.md` §13, `DATA_MODEL.md` → `Envelope`.
> Sequenced **before** transactions because P5's `envelope_contribution` validation needs the entity.

### ENV-1 — Keep the household's savings envelopes

**As a** household member
**I want** to name the big annual expenses we save for, set a yearly target in colones and/or dollars,
and choose when to be reminded
**So that** the dashboard can nudge us to top them up on the right months, and P5 can book
contributions against them

**Context / notes:** an envelope is a catalog entry with two extra facts — `annual_target_crc` /
`annual_target_usd` (either may be zero) and a `reminder_cadence` of `monthly` or `five_week_months`
(the extra-paycheck months). It holds **no running balance**: contributions are `envelope_contribution`
transactions (P5) and "contributed this month" is derived on the dashboard (P6). Same catalog rules as
categories and banks: unique per household case-insensitively, deactivate instead of delete, the
inactive clash offers **Reactivate** (restoring the stored name, applying the targets just typed),
another household's id is **404**. **Never seeded** — targets are personal amounts. Any member may edit.

**Acceptance criteria**

```gherkin
Scenario: A new household has no envelopes
  When I GET /api/envelopes
  Then I receive an empty list — nothing is seeded

Scenario: Creating an envelope
  When I POST /api/envelopes with name "  Marchamo ", annual_target_crc 718000, annual_target_usd 0, reminder_cadence " Five_Week_Months "
  Then I receive 201 with name "Marchamo" (trimmed), the targets, reminder_cadence "five_week_months" (normalized), is_active true

Scenario: Invalid requests write nothing
  When I POST with a blank name, a name over 100 characters, an unknown cadence, or a negative target
  Then I receive 400 "invalid_request" naming the field, and no row exists

Scenario: A name clash is a 409, case-insensitively
  Given "Marchamo" exists and is active
  When I POST name "MARCHAMO"
  Then I receive 409 "envelope_exists" with no existing_id

Scenario: An inactive clash offers reactivation
  Given "Marchamo" exists and is inactive
  When I POST name "marchamo"
  Then I receive 409 "envelope_exists_inactive" with existing_id and existing_name "Marchamo"
  When I PUT /api/envelopes/{existing_id} with name existing_name, new targets, and is_active true
  Then "Marchamo" is active again under its stored name with the new targets

Scenario: Rename, retarget, recadence, deactivate
  When I PUT /api/envelopes/{id} with new values and is_active false
  Then GET /api/envelopes omits it and GET /api/envelopes?include_inactive=true shows it inactive
  And renaming to another envelope's name is 409 "envelope_exists"; renaming to my own name is fine
  And a PUT to an id from another household is 404 and their row is unchanged

Scenario: The Envelopes page
  Given I am signed in
  When I open /envelopes (linked from Settings → Catalog)
  Then I see each envelope with its two targets, its reminder, an Active/Inactive badge and Edit
  When I click New, fill the name, targets and reminder, and Create
  Then it appears in the list; a negative target is refused before any request;
       an inactive clash shows Reactivate, which restores the entry
```

**Out of scope:** contributions and the `envelope_contribution` class (P5); the dashboard reminder and
"contributed this month / remaining" (P6).

**Definition of done:** tests first; Core.Tests (`EnvelopeReminderCadences`), Api.Tests slice tests
on Postgres (no seeding, create/normalize, invalid theory, 409 offer + restore, list filter, rename
clash / own name / 404, cross-tenant read AND write negatives, contributor) + HTTP (401, 201/400/200,
409 shape, 404), bUnit page tests; migration `AddEnvelopes` with RLS DDL; contributor; Postman folder;
QA-ENV-01..02 + regenerated PDFs; EN/ES resx; Settings link; merged, app working.

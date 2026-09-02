# Stories — <Epic Name>

> One file per epic. Register epic key(s) here. Stories use Gherkin acceptance criteria.
> This is an EXAMPLE showing the format — replace with real stories per project.

**Epic key:** `EXAMPLE`

---

### EXAMPLE-1 — <short title>

**As a** member of a tenant
**I want** <capability>
**So that** <benefit>

**Context / notes:** links to the relevant `FEATURES.md` flow and any `DATA_MODEL.md` derived
rule this depends on.

**Acceptance criteria**

```gherkin
Scenario: Happy path
  Given I am signed in as a member of a tenant
  And <some precondition>
  When <I take an action>
  Then <expected outcome>
  And <data stays scoped to my tenant>

Scenario: Unhappy path / edge case
  Given <precondition>
  When <invalid action or boundary condition>
  Then <graceful handling / error>
```

**Out of scope:** <what this story does NOT cover>
**Definition of done:** all scenarios pass; Core logic unit-tested; tenant-scoping verified;
merged with app in a working state; ADRs logged for any decisions.

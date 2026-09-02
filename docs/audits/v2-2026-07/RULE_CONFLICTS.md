# RULE_CONFLICTS.md

Log of disagreements between phases about `FOUNDATION_RULES.md` rules or proposed fixes. Per the suite's deference rule, a later phase may FLAG a rule for revision here but never silently overturn it; the original stands until **Phase 5** resolves the entry.

Each entry: `#n` · rule/finding · raised by (phase) · the disagreement · evidence · proposed resolution (for Phase 5).

---

## Phase 1

None. Phase 1 is the first pass — there are no prior rules to contradict. R1–R24 established in `FOUNDATION_RULES.md`.

---

## Phase 2

None. No Phase 1 rule was tool-disproven; all R1–R24 stand. Added R25–R27 (supply chain).

## Phase 3

None. Logic-bug and test-completeness findings ADD rules (R28–R31); no prior rule contradicted. Six open questions raised (LOGIC-B1/B2 intent, B5 clock, B7 guarantee strictness, B8 timezone, S3 invitation email-binding, S2 TOTP window) are **design/intent decisions for Phase 5 / human**, not rule conflicts.

---

## Phase 4

None. The adversarial slice-build ADDED R32–R35 (write-side UPDATE/DELETE guard, `QueryAllTenants` arch-ban, central-touchpoint reduction, route/table collision guard); no prior rule was tool-disproven. R32 **sharpens** (does not overturn) the implicit "write isolation is structural" claim behind R1/CONF-1 by showing the interceptor covers INSERT only — filed as a new rule, not a conflict, because CONF-1's verified scope was explicitly INSERT-stamping. R33 sharpens R5 (extends the ban set) without contradicting it.

One item flagged for Phase 5 review (not a conflict): **TR-8/DOC-17 doctrine wording** — `NotesDataContributor` and the `IRepository` docs sanction `QueryAllTenants()` in contributors while ADR-014 calls it "forbidden" for slices; R33's Features-path ban with a `*DataContributor.cs` allowlist is the mechanical reconciliation Phase 5 should adopt.

## Phase 5 — resolutions (final)

All entries resolved; recorded in `FOUNDATION_RULES.md` v1.0 §"Phase-5 conflict resolutions":
- **R5 ⊕ R33** merged — the `Features/**` ban covers both `IgnoreQueryFilters()` and `QueryAllTenants()`, allow-listing `*DataContributor.cs`.
- **TR-8 / DOC-17 doctrine** resolved — escape hatch forbidden in request-path slice code, required in `*DataContributor.cs`; docs name `QueryAllTenants()`/`EnterTenant`, never `IgnoreQueryFilters()`; ADR-014 wording amended (task B10-6). Precedence (d): machine ban + allowlist beats ambiguous prose.
- **R14 ⊆ R31** merged (TDD); **ARCH-1 ≡ SOLID-4** one rule (seam placement, R24).
- **0 irreconcilable Critical-vs-Critical conflicts** → no human escalation on rules. The six Phase-3 open questions + D7 are **intent decisions** (D1–D7 in `AUDIT_TASKS.md`), not rule conflicts; each blocks only its own task.

**Gate: all four reports pin `84c7ad8`.** Consolidation complete; `AUDIT_TASKS.md` is the approved-pending remediation plan.

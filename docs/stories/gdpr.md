# Stories — Account & data lifecycle (GDPR)

> One file per epic. **Data portability** (export "download my data") and **erasure** ("right to be
> forgotten"), assembled from machinery the platform already has: the `ITenantDataContributor` seam,
> the transactional **dissolve** flow, the **audit log** (ADR-008), and **file storage** (ADR-010).
> Design decision + constraints in **ADR-011**. Stories use Gherkin acceptance criteria.
> **Status: ✅ COMPLETE** — GDPR-1 (tenant export) + GDPR-2 (account erasure) both merged. **UI shipped**
> (`feat/ui-1-gdpr`): owner **Download household data** on Household + **Delete my account** in Settings
> (EN/ES; QA-HH-13, QA-SET-07).

**Epic key:** `GDPR`

**Prerequisites (external, before any code):**
- None to build/run — reuses existing infra. Deps satisfied: **audit** (ADR-008 ✅), **file storage**
  (ADR-010 ✅, for the export artifact), the **dissolve** flow, and the **permission seam** (ADR-009).
- No new packages.

**Reuses:** `ITenantDataContributor` (add `ExportAsync` beside `HasDataAsync`/`WipeAsync`),
`IFileStorage` (store the export, hand back a signed URL), `IAuditLog`, and the single-owner
transfer/dissolve invariants (ADR-003).

---

### GDPR-1 — Tenant data export ("download my data")

**Status: ✅ Implemented** (`feat/gdpr-1-tenant-export`). `ITenantDataContributor` gained
`ExportKey` + `ExportAsync` (Notes + Audit contributors implement it — secret-free); `TenantExportService`
(`src/Api/Services/`) assembles core (tenant/members/invitations, **token hashes omitted**) + each
contributor's section → JSON → `IFileStorage.PutAsync` → signed URL via `GetDownloadUrlAsync`; records
`tenant.exported`. Owner-only endpoint `POST /api/household/export` (new `Permission.ExportData`, → 403
for non-owner). **API-only** (no UI button yet). Tests `tests/Api.Tests/Gdpr/TenantExportTests.cs`
(bundle assembly + secret-exclusion + tenant-scoping via a capturing `IFileStorage`, owner 200 /
non-owner 403, audit) + matrix (`RolePermissionsTests`).

> **2026-07-15 — completeness fix (v3 audit LB-TEN-1).** Three `ITenantScoped` tables were wired into
> neither dissolve nor export — `ApiKey`, `UsageCounter`, `WebhookSubscription` — plus `WebhookDelivery`
> (a plain-`TenantId` table). With no FK to `Tenants` (ADR-003) nothing cascaded, so a dissolved tenant
> orphaned hashed key credentials + encrypted webhook secrets, and the export silently omitted them. Fixed
> by three new contributors (`ApiKeyDataContributor`, `WebhookDataContributor`, `UsageCounterDataContributor`,
> secret-free export) + a tenant-axis canary `EveryTenantOwnedEntity_IsWiredIntoTenantDissolution` (the
> mirror of the user-keyed erasure canary) so a new tenant-owned entity can't silently orphan again. Tests:
> `tests/Api.Tests/Gdpr/TenantTeardownContributorTests.cs`.

**As a** tenant owner
**I want** to download an export of my tenant's data
**So that** I can take it elsewhere or satisfy a data-portability request

**Context / notes:** extend [`ITenantDataContributor`](../../src/Core/Abstractions/ITenantDataContributor.cs)
with `ExportAsync(tenantId)` + an `ExportKey` (section name); each contributor returns a
JSON-serializable snapshot of its tenant data — the same "add a feature, no central edits" property as
wipe. A platform `TenantExportService` assembles the **core** (tenant, memberships + member emails,
pending invitations) plus every contributor's section into one JSON bundle, writes it via
[`IFileStorage`](../../src/Core/Abstractions/IFileStorage.cs) under a tenant-scoped key, and returns a
**signed, time-limited download URL** (ADR-010). Owner-only (new `Permission.ExportData`); the request
is audited. **No secrets** in the bundle (no token/OTP hashes, no card data).

**Acceptance criteria**

```gherkin
Scenario: Owner exports the tenant's data
  Given I am the tenant owner
  When I request a data export
  Then I get a signed, time-limited URL
  And following it downloads a JSON bundle containing the tenant, its members and invitations, and each feature's data

Scenario: Export is owner-only
  Given I am a member or admin (not the owner)
  When I request a data export
  Then I am refused (403)

Scenario: Export contains no secrets
  Given the tenant has logins, invitations and audit events
  When I inspect the export
  Then it contains identifiers and content but no password/OTP/token hashes or card data

Scenario: Export is tenant-scoped
  Given two tenants
  When one exports
  Then the bundle contains only that tenant's data (never the other's)

Scenario: The export request is audited
  When an export is produced
  Then an AuditEvent records the actor and the action
```

**Out of scope:** a scheduled/async export job (synchronous is fine at platform scale); per-user
personal-data export distinct from the tenant export (the member list already carries each user's
identity); CSV/other formats (JSON only).
**Definition of done:** tests first; `ExportAsync` on each contributor; assembly + `IFileStorage`
write + signed URL; owner-gate (403 for non-owner); tenant-scoping; secret-exclusion; audit; merged,
app working; ADR-011 referenced.

---

### GDPR-2 — Account erasure ("delete my account")

**Status: ✅ Implemented** (`feat/gdpr-2-account-erasure`). `AccountErasureService`
(`src/Api/Services/`) wipes the caller's identity (`User`/`UserLogin`/`RefreshToken`/`LoginToken` — the
last email-keyed) in one transaction, honoring the single-owner invariant (ADR-003): owner with other
members → `MustTransferFirst`; solo owner → dissolve (contributors wipe + core teardown), gated by a
`DissolveConfirmationRequired` confirm; member/admin → membership removed **without re-home** and
`account.erased` audited in the surviving tenant. Endpoint `DELETE /api/auth/me`
(`?confirm_dissolve=true`) on `AuthController` → 204 / 400 / 409 / 401. Tests
`tests/Api.Tests/Gdpr/AccountErasureTests.cs` (member keeps-tenant + audit, solo-owner confirm/dissolve,
owner-with-members blocked, no-confirm erases nothing, unknown user).

**As a** user
**I want** to delete my account and personal data
**So that** I can exercise my right to be forgotten

**Context / notes:** a self-service "delete my account" that removes the caller's **identity/PII**
(`User`, `UserLogin`, `LoginToken`, `RefreshToken`) in one audited transaction, honoring the
single-owner invariant (ADR-003): a **sole owner with other members must transfer first**; a **solo
owner's tenant is dissolved** (its data wiped via the contributors, reusing the dissolve path); a
**plain member is removed but not re-homed** (the account is going away, unlike leave). Tenant app data
stays with the tenant. **Audit survives** — actor ids remain (audit holds ids, never PII), so the
compliance record is not deleted by erasure.

**Acceptance criteria**

```gherkin
Scenario: A member deletes their account
  Given I am a non-owner member
  When I delete my account
  Then my user, logins, tokens and sessions are removed
  And I am removed from the tenant (not re-homed)
  And the tenant and its data are untouched for the remaining members

Scenario: A solo owner deletes their account
  Given I am the only member of my tenant
  When I delete my account (with the required confirmation)
  Then my identity is erased and the tenant is dissolved and its data wiped (one transaction)

Scenario: A sole owner with other members must transfer first
  Given I own a tenant that has other members
  When I try to delete my account
  Then I am told to transfer ownership first (the tenant can't be left ownerless)

Scenario: Erasure is audited and leaves no dangling PII
  When an account is erased
  Then an AuditEvent records the action with the actor id
  And existing audit events still reference that id (no PII), never the deleted email
```

**Out of scope:** admin-initiated erasure of another user (an ADMIN-backoffice concern); a grace
period / soft-delete + scheduled hard-delete (retention policy — deployment concern); export-on-erase
bundling (the user can export first via GDPR-1).
**Definition of done:** tests first; member/solo-owner/blocked-owner paths; identity rows removed;
tenant data correctly kept (member) or wiped (solo owner); single-owner invariant preserved; audited;
merged, app working; ADR-011 referenced.

---

## Slice plan (implementation map)

Ordered, each a mergeable vertical slice. TDD throughout.

1. ✅ **Tenant export (GDPR-1).** — DONE. `ExportAsync`/`ExportKey` on `ITenantDataContributor`;
   `TenantExportService` assembles core + contributor sections → `IFileStorage` → signed URL;
   owner-gated endpoint `POST /api/household/export` (`Permission.ExportData`); audited; secret-free.
   API-only (no UI button yet).
2. ✅ **Account erasure (GDPR-2).** — DONE. `AccountErasureService` + `DELETE /api/auth/me`: wipes
   identity rows in one transaction, honoring transfer-or-dissolve for owners and remove-without-re-home
   for members; audited; audit trail (actor ids) survives.

**Known sharp edges (from ADR-011):** export is **owner-only** + returned as a **signed URL** (not
inline); **no secrets** in the bundle; erasure **never strands a tenant ownerless** (transfer or
dissolve first); erasing a user removes **identity, not tenant data**; **audit survives** erasure
(actor ids, never PII).

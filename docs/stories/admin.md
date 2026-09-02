# Stories — Admin back-office + impersonation (`ADMIN`)

> One file per epic. A **platform-staff** surface (outside the tenant model) to inspect any tenant and
> "sign in as" a user for support — the **highest-blast-radius** feature, built entirely on existing
> guardrails (`ITenantContext.EnterTenant` for scoped in-tenant reads/writes, ADR-003; the audit log,
> ADR-008 — the global filter is never disabled). Design +
> constraints in **ADR-014**. Stories use Gherkin acceptance criteria. **Status: ✅ COMPLETE** — ADMIN-1
> (staff gate + cross-tenant inspection) + ADMIN-2 (short-lived audited impersonation) + ADMIN-3
> (staff announcements → notification fan-out). **UI shipped**
> (`feat/ui-4-admin`): a staff-only `/admin` console (`AdminConsole`) — tenant list/detail + **Sign in as**
> with an impersonation banner + **Stop** — gated by a new non-gating probe `GET /api/admin/me`
> (`{is_staff}`; allowlist stays config-only). EN/ES; QA-ADMIN-01..06.
> **Extended 2026-07-09 (QA pass, ADR-021):** announcements gained an optional **`user_ids` subset**
> (intersected with the roster) + a **platform-wide broadcast** (`POST /api/admin/announce-all`,
> 202 → outbox fan-out via `AdminBroadcastOutboxHandler`), and staff can **comp/revert a tenant's plan**
> (`PUT|DELETE /api/admin/tenants/{id}/subscription`; 409 when Stripe-backed). Console UI: member
> checkboxes on the roster, an "Announce to everyone" card, plan badge + comp/revert buttons.
> **Extended 2026-07-10 (ADR-021 addendum, `feat/admin-mfa-reset`):** staff can **reset a user's MFA**
> (`DELETE /api/admin/users/{userId}/mfa`) — the recovery path for a user who lost both the
> authenticator and the recovery codes. Wipes `UserMfa` + `MfaRecoveryCode` via
> `IMfaService.ResetAsync` (no code — admin-path only) after **out-of-band identity verification**;
> audited in the target's tenant (`admin.mfa.reset`) and the user is notified through the fan-out
> (in-app + email, `security.mfa_reset`). Console UI: confirm-gated **Reset MFA** button on the member
> row. QA-ADMIN-07.

> **2026-07-15 — admin-write correctness (v3 audit ADM-5 + LB-ADM-2).** (1) Comp/revert now keys its 409 on
> subscription **liveness** (`Subscription.IsProviderManaged` = has a Stripe id **and** status ≠ canceled),
> not id-presence — a canceled Stripe sub keeps its id forever, so the old check permanently locked a
> churned tenant (the exact goodwill-comp target) out of a comp/cleanup. (2) `announce` with an
> **explicitly-empty `user_ids: []`** now notifies **no one** (was: fell through to the whole tenant, max
> blast radius) — a missing/null list still means every member. Tests: `AdminControllerTests`
> (`Staff_CompCanceledSubscription…`, `Staff_RevertCanceledSubscription…`, `Staff_Announce_WithEmptyUserIds…`).

> **2026-07-15 — MFA-reset session revocation + broadcast attribution (v3 audit ADM-7 / ADM-6 / ADM-11).**
> (1) `DELETE /api/admin/users/{userId}/mfa` now **revokes the target's refresh tokens/sessions**
> (`IRefreshTokenService.RevokeAllUserTokensAsync`, in the same transaction) — a reset is also a
> compromise-recovery primitive, and an attacker's live sessions would otherwise survive the second-factor
> wipe. (2) `announce-all` (largest blast radius, no in-tenant audit) now carries the acting **`StaffUserId`
> in its durable outbox payload** so it's attributable. (3) A **tenant-less** user's MFA reset — which has
> no tenant-scoped audit row — now leaves a structured `ILogger` warning (`admin.mfa.reset by staff … for
> tenant-less user …`); a full platform-scoped audit sink stays deferred (the finding is Info-level).
> Tests: `Staff_ResetMfa_RevokesTargetsRefreshTokens`, `Staff_AnnounceAll_Payload_CarriesTheActingStaffId`.

**Epic key:** `ADMIN`

**Prerequisites (external, before any code):**
- **Platform-staff allowlist** in config (`Admin:StaffEmails`, via `.env`/env vars) — out-of-band, not
  settable from the app.
- Reuses: audit (ADR-008 ✅), the audited escape hatch (ADR-003 ✅), JWT issuance, RBAC (ADR-009 ✅).
  No new packages.

**Guardrails (ADR-014, amended by ADR-021):** the global tenant filter is **never loosened** (scoped
in-tenant reads/writes enter the target via `EnterTenant`, keeping the filter engaged); staff is
**config-only** (never a tenant role / app toggle); impersonation is **short-lived + non-refreshable +
audited**; admin is **read-only by default** over tenant data, with an **enumerated list of audited
writes** (ADR-021): announcements (per-tenant, optional member subset, and the platform-wide broadcast —
per-user notification rows through the normal fan-out), the subscription **comp/revert** (the one
tenant-data mutation; refused 409 whenever a live provider subscription exists — Stripe stays the source
of truth, ADR-006), and the **staff MFA reset** (`DELETE /api/admin/users/{userId}/mfa` — per-user
identity rows only, after out-of-band identity verification; audited in-tenant + user notified — ADR-021
addendum 2026-07-10). Any further admin write requires a new ADR/amendment.

---

### ADMIN-1 — Platform-staff gate + cross-tenant inspection

**Status: ✅ Implemented** (`feat/admin-1-staff-inspection`). `PlatformAdminSettings` (`Admin:StaffEmails`,
`.env`-documented) + `IPlatformStaffService.IsStaffAsync` (resolves email, checks the allowlist
case-insensitively, fails closed) + `AdminApiControllerBase.RequireStaffAsync` (401/403 gate).
`AdminController` (`GET /api/admin/tenants` list via `ITenantRepository.ListAllAsync` — non-scoped
tables, no hatch; `GET /api/admin/tenants/{id}` detail **enters the target tenant** via
`ITenantContext.EnterTenant` so scoped reads go through the normal filter, and records
`admin.tenant.viewed` **in that tenant**). Read-only; the global filter is never loosened. Tests
`tests/Api.Tests/Admin/` (`PlatformStaffServiceTests` allowlist; `AdminControllerTests` non-staff→403,
list-with-counts, detail+in-tenant-audit, unknown→404).

**As a** platform staff member
**I want** to list and inspect any tenant
**So that** I can support and debug across the whole platform

**Context / notes:** `PlatformAdminSettings` (`StaffEmails`) + `IPlatformStaffService.IsStaffAsync(userId)`
(resolves the caller's email, checks the allowlist, case-insensitive) + an **`AdminOnly`** gate that 403s
non-staff. `AdminController` (staff-gated): `GET /api/admin/tenants` (list — id, name, member count,
created) and `GET /api/admin/tenants/{id}` (detail — members + roles, subscription status, counts). The
list reads non-scoped tables directly (`ListAllAsync`, no hatch); the detail **enters the target tenant**
via `ITenantContext.EnterTenant` (ADR-003 amendment) so scoped reads go through the normal filter engaged,
never a disabled filter. **Every access audited** (`admin.tenant.viewed`, in the target's tenant).
Read-only.

**Acceptance criteria**

```gherkin
Scenario: Staff can list tenants
  Given I am on the platform-staff allowlist
  When I GET /api/admin/tenants
  Then I see every tenant (id, name, member count) — from the non-scoped tenant tables

Scenario: Non-staff are refused
  Given I am a normal user (not on the allowlist)
  When I call any /api/admin endpoint
  Then I get 403 Forbidden

Scenario: Staff membership is config-only
  Given the allowlist is set in config
  Then it cannot be changed through any app endpoint (no self-serve staff grant)

Scenario: Admin reads are audited
  When staff inspect a tenant
  Then an AuditEvent records the staff actor and the tenant viewed

Scenario: The global filter is never loosened
  Then admin tenant-detail reads enter the target tenant via EnterTenant (filter engaged), not a disabled filter
```

**Out of scope:** cross-tenant **writes beyond the ADR-021 enumeration** (announce/broadcast +
subscription comp/revert; anything else goes through impersonation, ADMIN-2, or a new ADR);
a metrics/analytics dashboard; a staff-management UI (allowlist is config); a platform-level
cross-tenant audit trail (the broadcast's known gap — deferred until a second cross-tenant action
needs it).
**Definition of done:** tests first; staff-gate allow/deny (403), list + detail via the hatch, config-only
staff, audit on access; merged, app working; ADR-014 referenced.

---

### ADMIN-2 — Impersonation ("sign in as")

**Status: ✅ Implemented** (`feat/admin-2-impersonation`). `IJwtTokenService.IssueImpersonationToken` mints
a **short-lived** (15-min) token carrying the target's identity + provider `"impersonation"` + an
**`impersonated_by`** claim (the staff id), scoped by the target's `tenant_id`. `POST
/api/admin/impersonate/{userId}` (staff-gated) returns `{ access_token, expires_in }` with **no refresh
token** (can't be extended), and records `admin.impersonation.started` **in the target's tenant** (via
`EnterTenant`). Unknown target → 404. Tests `AdminControllerTests` (non-staff→403; token carries
target + `impersonated_by` + `tenant_id`, 900s; audited in target's tenant; unknown→404).

> **2026-07-15 — hardening (v3 audit ADM-2 / ADM-8 / LB-ADM-1).** (1) The staff gate now **rejects
> impersonation tokens** (403 `impersonation_not_allowed`; the `/me` probe reports not-staff) — staff A
> impersonating staff B could previously act on the whole admin surface attributed to B. (2) The
> no-pref-writes-while-impersonating guard (ADR-022) is now **server-enforced** on `PUT /api/auth/theme|locale`
> (was client-only). (3) Every audit row now carries **`AuditEvent.ImpersonatedBy`** (nullable, migration
> `AuditEventImpersonatedBy`), stamped ambiently by `AuditLog` from the JWT claim via the new
> `ICurrentImpersonation` seam — so writes made during an impersonation window are durably attributable to
> the real actor (previously only the session *start* was recorded); the tenant export includes it. Tests:
> `ImpersonationGuardTests` (integration, real tokens) + `AdminControllerTests` gate/probe cases.

**As a** platform staff member
**I want** a short-lived "sign in as" for a user
**So that** I can reproduce and fix an issue from their point of view

**Context / notes:** `POST /api/admin/impersonate/{userId}` (staff-gated) mints a **short-lived** access
token for the target (their claims + an **`impersonated_by`** claim = the staff user id) with **no refresh
token** — so it expires on its own and can't be extended. The token scopes via the target's `tenant_id`
claim (no filter bypass). The action is **loudly audited** (`admin.impersonation.started`) **in the
target's tenant**, so that tenant sees a platform admin accessed the account. Returns
`{ access_token, expires_in }`.

**Acceptance criteria**

```gherkin
Scenario: Staff impersonates a user
  Given I am staff
  When I POST /api/admin/impersonate/{userId} for an existing user
  Then I get a short-lived access token carrying that user's identity and an impersonated_by claim
  And no refresh token is issued (it can't be extended)

Scenario: Impersonation is audited in the target's tenant
  When I start impersonation
  Then an AuditEvent records me as actor, the target user, in the target's tenant

Scenario: Non-staff cannot impersonate
  Given I am a normal user
  When I try to impersonate anyone
  Then I get 403 Forbidden

Scenario: Unknown target
  When I impersonate a non-existent user
  Then I get 404 and no token is issued
```

**Out of scope:** a "stop impersonating / return to admin" flow (the short-lived token just expires);
restricting which users can be impersonated (e.g. not other staff) — a policy a real deployment may add;
a UI banner (client concern).
**Definition of done:** tests first; staff-only (403), token carries target identity + `impersonated_by`
+ short expiry + no refresh, unknown target 404, audit in the target's tenant; merged, app working;
ADR-014 referenced.

---

### ADMIN-3 — Staff announcement to a tenant's members

**Status: ✅ Implemented** (`feat/admin-3-announcements`). `POST /api/admin/tenants/{id}/announce`
(`AdminAnnounceRequest` title ≤200 / body ≤2000 → 400; staff-gated → 403; unknown tenant → 404);
inside one transaction + `EnterTenant`: `NotifyAsync(userId, "announcement", …)` per member (in-app
row and/or outbox email per each user's prefs) + `admin.announcement.sent` audited in-tenant with
`member_count`; returns `{notified_count}`. Console UI: send-announcement card in the tenant detail
(confirm dialog, EN/ES). E2E `AnnouncementJourneyTests` (staff sends → target's bell badge + item →
mark-read clears; non-staff forbidden) — the suite's first browser-triggerable notification producer,
closing QA-NOTIF-01/02 automation. **Found & fixed en route:** `AuthService.IsStaffAsync` called
`/api/admin/me` on the Bearer-less auth client, so the web admin console was unreachable (401 → always
"forbidden"); it now attaches the in-memory token explicitly. Maps to QA-ADMIN-04.

**As a** platform staff member
**I want** to send an announcement to all members of a tenant
**So that** I can notify affected users (maintenance, incidents) through the app's normal channels

**Context / notes:** A real support capability that doubles as the browser-triggerable notification
producer the E2E suite lacked. Delivery is the existing NOTIFY fan-out (ADR-013) — per-user prefs
decide in-app vs email; nothing is hard-coded. The write is per-user notification rows only; tenant
data is untouched (guardrail above). Audited in the target tenant like every admin action.

**Acceptance criteria**

```gherkin
Scenario: Staff announcement reaches every member
  Given a tenant with N members
  When staff sends an announcement (title + body)
  Then each member gets a notification through their preferred channels
  And the response reports N notified

Scenario: The announcement is audited in the target tenant
  When staff sends an announcement
  Then an admin.announcement.sent AuditEvent (with member_count) lands in that tenant

Scenario: Non-staff cannot announce
  Given a normal user
  When they call the announce endpoint
  Then they get 403 Forbidden

Scenario: Validation
  When title/body are missing or exceed 200/2000 chars
  Then the request is rejected with 400
```

**Out of scope:** targeting a single member (announcements are tenant-wide; impersonation covers 1:1
support); scheduling/drafts; cross-tenant broadcast to ALL tenants (loop in the console if ever needed).
**Definition of done:** tests first; fan-out + audit + gating + validation covered; console UI with
confirm; E2E journey green; merged, app working; ADR-014 addendum recorded.

---

## Slice plan (implementation map)

Ordered, each a mergeable vertical slice. TDD throughout.

1. ✅ **Staff gate + inspection (ADMIN-1).** — DONE. `PlatformAdminSettings` (config allowlist) +
   `IPlatformStaffService` + `AdminApiControllerBase.RequireStaffAsync` (403); `AdminController`
   list (non-scoped tables) + detail (via `EnterTenant`, audited in-tenant). Read-only; global filter
   never loosened. (Chose `EnterTenant` over `QueryAllTenants` — keeps the filter engaged, scoped.)
2. ✅ **Impersonation (ADMIN-2).** — DONE. `IJwtTokenService.IssueImpersonationToken` +
   `POST /api/admin/impersonate/{userId}` → 15-min, non-refreshable, `impersonated_by`-tagged access
   token; audited (`admin.impersonation.started`) in the target's tenant. Unknown target → 404.
3. ✅ **Announcements (ADMIN-3).** — DONE. Staff announcement → NOTIFY fan-out to all members +
   in-tenant audit, console form, E2E journey (the suite's notification producer); fixed the
   Bearer-less `IsStaffAsync` probe that made the web console unreachable.

**Known sharp edges (from ADR-014):** the global filter is **inviolable** (`EnterTenant` keeps it engaged; never disabled); staff is
**config-only** (never a role/app toggle); impersonation is **short-lived + non-refreshable + audited**;
admin is **read-only** over tenant data; **no secrets/PII** in responses or audit metadata.

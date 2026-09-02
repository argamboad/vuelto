# Stories — RBAC (admin role + permission seam)

> One file per epic. Adds a third tenant role (`admin`) **and** a permission seam so authorization
> call sites ask *"can the caller do X?"* (a `Permission` capability) instead of *"is the caller the
> owner?"*. Design decision, the permission matrix, and constraints in **ADR-009**. Stories use
> Gherkin acceptance criteria. **Status: ✅ COMPLETE** — RBAC-1 (seam + matrix) ✅, RBAC-2
> (owner-only role-change endpoint) ✅, RBAC-3 (admin-aware Household roster UI) ✅.

**Epic key:** `RBAC`

**Prerequisites (external, before any code):**
- None. Self-contained — extends the existing `TenantMembership.Role` + `TenantApiControllerBase`
  helpers; reuses the `.RequireEntitlement(...)` endpoint-filter shape (ADR-006) and the `IAuditLog`
  (ADR-008). No new packages.

**Role model (ADR-009):** `owner` > `admin` > `member`. The "exactly one owner" invariant (ADR-003)
is unchanged — owner is conferred only via `TransferOwnershipAsync`, never via the role-change
endpoint. Role is read **live from membership**, never from the JWT, so changes take effect on the
next request with no token refresh.

**Permission matrix (ADR-009):**

| Permission | owner | admin | member |
|---|:--:|:--:|:--:|
| `ViewTenant` | ✅ | ✅ | ✅ |
| `RenameTenant` | ✅ | ✅ | ❌ |
| `ManageMembers` | ✅ | ✅ | ❌ |
| `ManageRoles` | ✅ | ❌ | ❌ |
| `ManageBilling` | ✅ | ❌ | ❌ |
| `TransferOwnership` / `DissolveTenant` | ✅ | ❌ | ❌ |

---

### RBAC-1 — Permission seam + `admin` role + enforcement helpers

**Status: ✅ Implemented** (`feat/rbac-1-permission-seam`). `TenantRoles.Admin` added; `Permission`
enum + `RolePermissions` matrix in **Core** (`src/Core/Authorization/`); enforcement via
`RequirePermission(membership, perm)` on
[`TenantApiControllerBase`](../../src/Api/Controllers/TenantApiControllerBase.cs) (→ 403 envelope) and
a `.RequirePermission(perm)` minimal-API filter
([`PermissionEndpointExtensions`](../../src/Api/Features/PermissionEndpointExtensions.cs), → 403)
backed by `IPermissionService`/`PermissionService` (fail-closed). The `IsOwner` checks in
`HouseholdController`, `HouseholdInvitationsController`, and `BillingController` were refactored onto
the seam (behavior unchanged — only owners exist until RBAC-2); `IsOwner` removed (one enforcement
path). Tests: `tests/Core.Tests/RolePermissionsTests.cs` (matrix), `tests/Api.Tests/Rbac/`
(filter allow/deny + `PermissionService` resolution/fail-closed).

> **2026-07-15 — deterministic authz membership lookup (v3 audit LB-ADM-3).** Authz resolution
> (`PermissionService` + `RequireTenantPermissionAttribute`) used an **unfiltered, unordered**
> `GetMembershipAsync(userId)` `FirstOrDefault` and never compared the resolved membership's tenant to the
> caller's JWT `tenant_id`. Both now resolve `GetMembershipAsync(userId, tenantId)` **keyed on the JWT
> `tenant_id`** (new `ClaimsPrincipal.GetTenantId()`), and the single-arg lookup is now **ordered**
> (oldest first) so it's never arbitrary. A DB unique index on `UserId` enforces one membership per user
> today, so the "resolves an arbitrary membership" scenario can't actually occur — this is defense-in-depth
> that also fails **closed** when a token's `tenant_id` doesn't match the user's membership (stale/forged),
> instead of silently authorizing against the user's other-tenant role. Test:
> `PermissionServiceTests.Authz_ResolvesForTheJwtTenant_AndFailsClosedOnAMismatch`.

**As a** platform/app developer
**I want** authorization expressed as capability checks backed by one role→permission matrix
**So that** I can add an `admin` tier and gate any endpoint without copying `role == "owner"` tests

**Context / notes:** add `TenantRoles.Admin`; a `Permission` enum and a static `RolePermissions`
matrix in **Core** (the single source of truth — ADR-009). Enforcement comes in two shapes mirroring
the two API styles (ADR-004): a `RequirePermission(membership, Permission.X)` helper on
[`TenantApiControllerBase`](../../src/Api/Controllers/TenantApiControllerBase.cs) that returns the
standard **403** envelope, and a `.RequirePermission(Permission.X)` minimal-API endpoint filter that
mirrors [`.RequireEntitlement(...)`](../../src/Api/Features/EntitlementEndpointExtensions.cs) but
yields **403 Forbidden** (authorization) rather than 402 (payment). Refactor the existing `IsOwner`
checks in `HouseholdController`, `HouseholdInvitationsController`, and `BillingController` onto the
seam. **Behavior is unchanged** (only owners exist until RBAC-2), but the seam is in place and tested.

**Acceptance criteria**

```gherkin
Scenario: The matrix maps roles to capabilities
  Given the RolePermissions matrix
  Then owner has every permission
  And admin has ViewTenant, RenameTenant and ManageMembers but not ManageRoles, ManageBilling, TransferOwnership or DissolveTenant
  And member has only ViewTenant

Scenario: An endpoint gated by a permission allows a role that has it
  Given an endpoint guarded by RequirePermission(ManageMembers)
  When an owner calls it
  Then the request proceeds

Scenario: An endpoint gated by a permission rejects a role that lacks it
  Given an endpoint guarded by RequirePermission(ManageMembers)
  When a member calls it
  Then the response is 403 Forbidden with the standard error envelope

Scenario: Existing owner-only endpoints keep their behavior
  Given the household rename / invitation / billing endpoints refactored onto RequirePermission
  When a non-owner with no admin role calls them
  Then they are still rejected exactly as before
```

**Out of scope:** assigning the admin role (RBAC-2); resource-level ACLs (not planned — ADR-009).
**Definition of done:** tests first; matrix unit-tested; both enforcement paths (controller helper +
minimal-API filter) tested for allow/deny; the refactored endpoints proven unchanged; merged, app
working; ADR-009 referenced.

---

### RBAC-2 — Change a member's role (promote/demote)

**Status: ✅ Implemented** (`feat/rbac-2-role-change`). Owner-only `PUT /api/household/members/{userId}/role`
on [`HouseholdController`](../../src/Api/Controllers/HouseholdController.cs) gated by
`Permission.ManageRoles`; `TenantService.ChangeMemberRoleAsync` enforces the ADR-009 invariants (owner
never set/cleared here — `InvalidRole`/`CannotChangeOwner`; no self-change; idempotent no-op) and
records `member.role_changed` via `IAuditLog` **atomically** with the update (staged on the shared unit
of work — first real audit producer). Member-removal hardened: `RemoveMemberResult.CannotRemoveOwner`
so an admin (who now holds `ManageMembers`) can't orphan the tenant. Tests
`tests/Api.Tests/Rbac/MemberRoleManagementTests.cs` (promote/demote + audit, every invariant, 403 for
non-owner, 404, idempotency, owner-removal guard). **API-only** — the roster promote/demote UI is the
RBAC-3 follow-up.

**As a** tenant owner
**I want** to promote a member to admin and demote an admin back to member
**So that** I can delegate administration without handing over ownership

**Context / notes:** an owner-only endpoint (`ManageRoles`) that moves a target user between `admin`
and `member`. Enforces the ADR-009 invariants: **never** sets or clears `owner` (ownership transfer
stays its own flow), never targets the owner, never lets a caller change their own role, and rejects
no-op/unknown roles. The change is **audited** (ADR-008) — actor, target, old→new role. The roster
(`GET /api/household`) already returns each member's role, so the UI can render the control.

**Acceptance criteria**

```gherkin
Scenario: Owner promotes a member to admin
  Given I am the owner and Bob is a member
  When I set Bob's role to admin
  Then Bob's membership role is admin
  And an AuditEvent records me as actor, Bob as target, member -> admin

Scenario: Owner demotes an admin to member
  Given I am the owner and Bob is an admin
  When I set Bob's role to member
  Then Bob's membership role is member
  And the change is audited

Scenario: The owner role cannot be assigned or removed here
  Given I am the owner
  When I try to set any member's role to owner, or to change the owner's role
  Then the request is rejected (ownership transfer is a separate flow)

Scenario: Non-owners cannot change roles
  Given I am an admin or a member
  When I try to change anyone's role
  Then the response is 403 Forbidden

Scenario: A caller cannot change their own role
  Given I am the owner
  When I try to change my own role
  Then the request is rejected
```

**Out of scope:** bulk role changes; custom/named roles beyond owner/admin/member; an admin-management
UI beyond the existing roster (a Shared.Ui follow-up).
**Definition of done:** tests first; promote/demote happy paths, every invariant (no owner via this
path, no self-change, non-owner 403), and the audit record tested on the Postgres Testcontainer;
the roster reflects the new role; merged, app working; ADR-009 referenced.

---

## Slice plan (implementation map)

Ordered, each a mergeable vertical slice. TDD throughout.

1. ✅ **Permission seam (RBAC-1).** — DONE. `TenantRoles.Admin`; `Permission` enum + `RolePermissions`
   matrix (Core); `RequirePermission(membership, perm)` on `TenantApiControllerBase` (→ 403 envelope)
   and a `.RequirePermission(perm)` minimal-API endpoint filter (sibling of `RequireEntitlement`, →
   403) backed by `IPermissionService`. Refactored the `IsOwner` call sites onto it and removed
   `IsOwner` (one enforcement path). No behavior change (owner passes everything).
2. ✅ **Role-change endpoint (RBAC-2).** — DONE. Owner-only `PUT /api/household/members/{userId}/role`
   moving a target between `admin`/`member`; ADR-009 invariants enforced (owner never via this path, no
   self-change, idempotent no-op); audited via `IAuditLog` atomically; member-removal hardened against
   removing the owner. API-only (roster UI deferred to RBAC-3). Makes the RBAC-1 seam live.

3. ✅ **Roster role-management UI (RBAC-3).** — DONE. Owner-only **Make admin / Make member** controls
   on the Household roster (`Shared.Ui/Pages/Household.razor`), wired to `PUT …/members/{id}/role`, with
   EN/ES strings + an **Admin** badge. Made the page **admin-aware** (client mirror of the matrix:
   admin can rename + invite/remove members, but sees no role/transfer/dissolve controls; the owner row
   never shows actions). Web QA cases **QA-HH-09..12** + both PDFs regenerated. Added after RBAC-2
   shipped API-first.

**Known sharp edges (from ADR-009):** keep the **exactly-one-owner** invariant — the role endpoint
never touches `owner`; **no self-escalation / no lockout** (can't change own role, admin can't act on
owner); permissions are **coarse capabilities, not resource ACLs** (don't grow into an ACL system
without a new ADR); the **matrix is the only place** roles map to capabilities — add a `Permission` +
a row, never a scattered `role == "admin"` check.

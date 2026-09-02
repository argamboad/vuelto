# Data Model

> Structural source of truth: entities, fields, relationships, and **derived rules** (computed,
> not stored). Stack-agnostic; concrete EF Core migrations follow in the repo. The multi-tenant
> base entities are constant; everything else is app-specific.

## Conventions
- `id` primary key on every entity unless noted. UUIDv7 (`Guid.CreateVersion7()`) used — time-ordered,
  supported in .NET 9+ and already active in base entities.
- Timestamps (`created_at`, `updated_at`) assumed on all entities; omitted below for brevity.
- **Tenant scoping:** every app entity that holds tenant data implements `ITenantScoped`
  (a `TenantId`) and is filtered automatically by a global EF query filter (see ADR-003) — you
  can't forget to scope a read. Genuinely cross-tenant/pre-auth reads use the sanctioned escape
  hatch **`IRepository<T>.QueryAllTenants()`** (audited; used by dissolve contributors), and a
  signature-/system-authenticated tenant-scoped write enters its tenant via
  **`ITenantContext.EnterTenant(tenantId)`** — `IgnoreQueryFilters()` is **banned in
  `src/Api/Features/**`** (a build-time test enforces it). Never leak across tenants.
- "Tenant" is the code term for the household/org/team. The reference implementation labels it
  **Household**; rename per app.

## Base entities (constant — multi-tenant foundation)

### Tenant
The household/org/team. Owns all tenant-scoped data.
- `id` (UUIDv7)
- `name`
- _TODO: tenant-level fields specific to this app_

### User
A person (identity). A user belongs to exactly one tenant **via `TenantMembership`** — there is
**no `tenant_id` on User**. Passwordless-capable; no password is stored.
- `id` (UUIDv7) — a plain POCO, **not** `IdentityUser`
- `email` (unique, normalized lower-case), `display_name` (nullable, refreshed from the provider)
- `email_verified` — true only when a provider asserts a verified email (fail-closed default; this
  guards the credential-attachment takeover)
- `locale` (nullable) — per-user UI language preference
- `theme` (nullable) — per-user UI theme ("light"/"dark"/"system", stored verbatim; null = never
  chose, which lets sign-in adopt a device-local choice — PREFS-1, ADR-022)
- `logins` — navigation to `UserLogin`

### UserLogin
One OAuth identity linked to a `User` (a user may link several providers).
- `id` (UUIDv7), `user_id` (FK → User)
- `provider` ("google", "microsoft", …), `provider_user_id`
- unique on (`provider`, `provider_user_id`)

### TenantMembership *(the user→tenant link — source of truth for tenancy)*
- `id` (UUIDv7), `tenant_id` (FK → Tenant), `user_id` (FK → User)
- **unique on `user_id`** — a user is in exactly one tenant at a time
- `role` — `owner` | `admin` | `member` (exactly one owner per tenant; `admin` is a delegated-management
  tier — ADR-009), `joined_at`. Capabilities per role are defined in `RolePermissions`, not ad-hoc checks.

### RefreshToken
A rotating, hashed refresh token backing a session — only the **hash** is stored, so a DB leak
can't forge sessions.
- `id` (UUIDv7), `user_id`, `token_hash` (SHA-256), `provider`
- `issued_at`, `expires_at`, `is_revoked`, `issued_from_ip`

### LoginToken *(passwordless: magic link + email OTP)*
A single-use, hashed, time-limited credential. The account is resolved/created at redemption, so a
typo'd or probed email leaves no account behind.
- `id` (UUIDv7), `email`, `code_hash` (SHA-256), `purpose` (`magic-link` | `otp`)
- `created_at`, `expires_at`, `consumed_at` (nullable), `attempt_count` (OTP lockout)
- **Derived (computed, never stored):** `is_expired`, `is_consumed`, `is_valid`

### UserMfa *(MFA — authenticator TOTP; ADR-012)*
A user's TOTP second-factor state (one per user). User-scoped identity data (wiped by account erasure).
- `id` (UUIDv7), `user_id` (unique) — one MFA row per user
- `encrypted_secret` — the TOTP secret **encrypted at rest** (Data Protection); never plaintext, never
  returned after enrollment
- `enabled` (true only after a valid code confirms possession), `enrolled_at`
- `last_verified_time_step` (nullable) — the TOTP time-step accepted by the most recent successful
  **login** step-up; a code whose step is ≤ this is rejected as a replay (RFC-6238 anti-replay; v2
  audit LOGIC-S1). Null until the first login step-up; enrollment-confirm deliberately does not set it.

### MfaRecoveryCode *(MFA — ADR-012)*
Single-use recovery codes, stored **only as hashes** (SHA-256); the raw codes are shown once at
enrollment. User-scoped (wiped by account erasure).
- `id` (UUIDv7), `user_id`, `code_hash`, `used_at` (nullable — consumed when set)

### Notification *(in-app notifications — ADR-013)*
A per-user notification. **Keyed by `user_id` — NOT tenant-scoped** (the ADR-C2 per-user carve-out); a
user only ever sees their own. User PII (wiped by account erasure).
- `id` (UUIDv7), `user_id`, `kind` (stable verb), `title`, `body`
- `metadata` (jsonb, nullable — identifiers only, no secrets), `read_at` (nullable), `created_at`
- indexed on `(user_id, created_at)` for the newest-first feed + unread counts

### NotificationPreference *(in-app notifications — ADR-013)*
Per-user delivery preferences (the ADR-C2 per-user carve-out, like `User.Locale`). One row per user;
absence ⇒ both channels on. User PII (wiped by account erasure).
- `id` (UUIDv7), `user_id` (unique), `in_app_enabled`, `email_enabled` (both default true)

### TenantInvitation *(constant — auth foundation)* — implements `ITenantScoped`
An email invitation to join a tenant. The raw token is revealed once at creation; only its hash is
stored.
- `id` (UUIDv7), `tenant_id` (FK → Tenant)
- `invited_email` — normalized lower-case
- `token_hash` (SHA-256) — **no raw token column**
- `invited_by_user_id` (Guid), `status` (`pending` | `accepted` | `revoked` | `expired`)
- `created_at`, `expires_at`

**Derived rules (computed, never stored):**
- `is_expired` → `now > expires_at`
- `is_valid` → `status == pending AND !is_expired`

## App entities
<!-- Design fresh per app. For each entity: fields, relationships, and tenant_id where it holds
     tenant data. Do NOT copy entity designs from other projects. -->
_TODO_

## Relationship summary
- Tenant 1 — N TenantMembership N — 1 User *(constant; unique on `user_id` = one tenant per user)*
- User 1 — N UserLogin *(constant)*
- User 1 — N RefreshToken *(constant)*
- Tenant 1 — N TenantInvitation *(constant)*
- LoginToken is keyed by email (no FK — the account is resolved at redemption) *(constant)*
- _TODO: app-specific relationships_

### ER diagram — identity & auth foundation

Drawn from the code (entities in `src/Core/Entities/`, constraints in
`src/Infrastructure/Persistence/Configurations/`). **Solid lines are real FK constraints (there
are only four in the whole model, all cascade); dotted lines are logical, application-enforced
Guid references with no DB constraint.** Invariants are annotated on the columns they protect.
Decision context: [ADR-002](DECISIONS.md) (custom JWT auth), [ADR-003](DECISIONS.md)
(membership-based tenancy), [ADR-012](DECISIONS.md) (MFA), [ADR-013](DECISIONS.md) (notifications).

```mermaid
erDiagram
    TENANT ||--o{ TENANT_MEMBERSHIP : "FK cascade"
    USER ||--o| TENANT_MEMBERSHIP : "FK cascade - unique user_id = one tenant per user"
    TENANT ||--o{ TENANT_INVITATION : "FK cascade"
    USER ||--o{ USER_LOGIN : "FK cascade"
    USER ||..o{ REFRESH_TOKEN : "user_id - no FK"
    USER ||..o| USER_MFA : "user_id unique - no FK"
    USER ||..o{ MFA_RECOVERY_CODE : "user_id - no FK"
    USER ||..o{ NOTIFICATION : "user_id - no FK"
    USER ||..o| NOTIFICATION_PREFERENCE : "user_id unique - no FK"
    USER ||..o{ LOGIN_TOKEN : "by email string only - account resolved at redemption"

    TENANT {
        guid id PK
        string name
    }
    USER {
        guid id PK
        string email UK "unique, normalized lower-case"
        bool email_verified "fail-closed: true only when a provider asserts it"
        string locale "nullable - per-user pref"
        string theme "nullable - stored verbatim (ADR-022)"
    }
    TENANT_MEMBERSHIP {
        guid tenant_id FK
        guid user_id FK,UK "unique - a user is in exactly one tenant"
        string role "owner | admin | member - exactly one owner per tenant"
    }
    TENANT_INVITATION {
        guid tenant_id FK
        string invited_email
        string token_hash UK "SHA-256 - no raw token column"
        string status "pending | accepted | revoked (expired is computed, never written)"
        datetimeoffset expires_at
    }
    USER_LOGIN {
        guid user_id FK
        string provider "unique with provider_user_id"
        string provider_user_id
    }
    REFRESH_TOKEN {
        guid user_id
        string token_hash UK "SHA-256 - DB leak cannot forge sessions"
        bool is_revoked "revoked-but-unexpired rows kept for reuse detection"
        datetimeoffset expires_at
    }
    LOGIN_TOKEN {
        string email "no user FK"
        string code_hash "SHA-256, single-use"
        string purpose "magic-link | otp"
        datetimeoffset consumed_at "nullable - set once, atomically"
        int attempt_count "OTP lockout counter"
    }
    USER_MFA {
        guid user_id UK "one MFA row per user"
        string encrypted_secret "Data Protection - never plaintext"
        bool enabled
        long last_verified_time_step "TOTP anti-replay watermark"
    }
    MFA_RECOVERY_CODE {
        guid user_id
        string code_hash "peppered HMAC - low-entropy secret"
        datetimeoffset used_at "nullable - single-use"
    }
    NOTIFICATION {
        guid user_id "per-user carve-out, NOT tenant-scoped"
        string kind "security.* kinds are non-suppressible"
        datetimeoffset read_at "nullable"
    }
    NOTIFICATION_PREFERENCE {
        guid user_id UK
        bool in_app_enabled "absence of row = both channels on"
        bool email_enabled
    }
```

## Derived rules (computed, never stored)
<!-- The domain logic specific to this app. TenantInvitation rules are defined above. -->
_TODO_

## Platform entities (built — ADRs 006–016)

> These are the tenant-/platform-scoped tables the platform epics added. All have EF Core migrations.
> New app/domain tables you add should implement `ITenantScoped` (so the global tenant filter covers
> them) and register an **`ITenantDataContributor`** (with `ExportKey` + `ExportAsync` **and**
> `HasDataAsync`/`WipeAsync`) so they participate in tenant export + dissolve — there is **no** central
> `HasDataAsync`/`WipeDataAsync` method to edit (adding a feature never means touching central code).

- **`Subscription`** *(ADR-006 / `docs/stories/billing.md`)* — ✅ **BUILT (BILLING-1..7)**:
  `src/Core/Entities/Subscription.cs`, migration `AddSubscription`. `ITenantScoped`, **unique per
  tenant**; `plan_key`, `status`, `stripe_customer_id`/`stripe_subscription_id`, `current_period_end`,
  `lapse_notified_at` (nullable — set by the BILLING-6 lapse sweep so it nudges once per lapse),
  `last_event_at` (nullable — the timestamp of the most recently *applied* webhook event; the handler
  applies an incoming event only if strictly newer, so a redelivered/out-of-order older event can't
  clobber newer state — v2 audit LOGIC-B1). A **projection** of Stripe state (Stripe is the source of
  truth for money); absent ⇒ Free tier (fail-closed), as is any non-active/lapsed status. Plan catalog
  is code (`src/Core/Billing/PlanCatalog.cs`), not a table. Participates in dissolve via
  `BillingDataContributor` (cancels the provider sub + wipes the projection).
- **`UsageCounter`** *(ADR-006 / `docs/stories/billing.md`)* — ✅ **BUILT (BILLING-5)**:
  `src/Core/Entities/UsageCounter.cs`. `ITenantScoped`. A per-tenant, per-period metered-usage counter:
  `key` (the metered action, e.g. "export"), `period` (a calendar month `yyyy-MM`, UTC), `count`,
  `updated_at`. One row per (tenant, key, period) — the month-keyed period makes it **self-resetting
  with no reset job**. `IQuotaService.TryConsumeAsync` increments it and denies once the plan's monthly
  limit is reached.
- **`ApiKey`** *(ADR-015 / `docs/stories/pubapi.md`)* — ✅ **BUILT (PUBAPI-1)**:
  `src/Core/Entities/ApiKey.cs`. `ITenantScoped`. A tenant-scoped API key for programmatic access — only
  the **hash** is stored: `name`, `key_hash` (deterministic hash for O(1) lookup), `prefix` (short
  non-secret display prefix), `scopes` (comma-separated), `created_by_user_id`, `created_at`,
  `last_used_at`, `expires_at` (nullable), `revoked_at` (nullable). The raw key is shown once at
  creation. A presented key authenticates as its tenant (mints a `tenant_id`-claim principal).
- **`WebhookSubscription`** *(ADR-016 / `docs/stories/hooks.md`)* — ✅ **BUILT (HOOKS-1)**:
  `src/Core/Entities/WebhookSubscription.cs`. `ITenantScoped`. A tenant's outbound webhook subscription:
  `url`, `event_types` (comma-separated), `encrypted_secret` (the HMAC signing secret **encrypted** at
  rest via Data Protection — needed in plaintext to sign, so it can't be hashed; revealed once),
  `created_by_user_id`, `created_at`, `disabled_at` (nullable). Delivery goes through the outbox
  (ADR-007), so it's durable + retried.
- **`WebhookDelivery`** *(ADR-016 / `docs/stories/hooks.md`)* — ✅ **BUILT (HOOKS-2)**:
  `src/Core/Entities/WebhookDelivery.cs`. **NOT `ITenantScoped`** — like `OutboxMessage` it's written
  from the tenant-less outbox dispatcher, so `tenant_id` is a **plain filter column** the read side
  filters on, not a global-filter scoping key. A per-attempt delivery record (retries add rows):
  `subscription_id`, `event_type`, `event_id`, `body` (the exact JSON sent — retained so a delivery can
  be **replayed**), `success`, `status_code` (nullable), `error` (nullable), `created_at`. Success rows
  commit atomically with the outbox `sent` flip; **failure rows are written through a fresh out-of-band
  context** so they survive the processor's rollback (2026-08-24 fix — staged failure rows were being
  discarded, leaving the log success-only).
- **`OutboxMessage`**, **`InboxMessage`**, **`AuditEvent`** — see below.

### ER diagram — platform entities (as built)

Drawn from the code. Every dotted line is a **logical** tenant/subscription reference (plain Guid
column, no FK constraint); the platform relies on the global query filter + RLS for isolation, not
on referential integrity to `Tenant`. Decision context: [ADR-006](DECISIONS.md) (billing),
[ADR-007](DECISIONS.md) (outbox/inbox), [ADR-008](DECISIONS.md) (audit),
[ADR-015](DECISIONS.md) (API keys), [ADR-016](DECISIONS.md) (webhooks).

```mermaid
erDiagram
    TENANT ||..o| SUBSCRIPTION : "unique tenant_id - at most one"
    TENANT ||..o{ USAGE_COUNTER : "unique(tenant,key,period)"
    TENANT ||..o{ API_KEY : ""
    TENANT ||..o{ WEBHOOK_SUBSCRIPTION : ""
    TENANT ||..o{ AUDIT_EVENT : "append-only"
    WEBHOOK_SUBSCRIPTION ||..o{ WEBHOOK_DELIVERY : "subscription_id - no FK"

    SUBSCRIPTION {
        guid tenant_id UK "ITenantScoped - projection of Stripe state"
        string plan_key "free | pro - absent row = Free (fail-closed)"
        string status "active | trialing | past_due | canceled"
        datetimeoffset last_event_at "recency guard - older webhook events never applied"
        datetimeoffset lapse_notified_at "nullable - one nudge per lapse"
    }
    USAGE_COUNTER {
        guid tenant_id "ITenantScoped"
        string key "metered action, e.g. export"
        string period "yyyy-MM - month key makes it self-resetting"
        int count "atomic conditional increment only"
    }
    API_KEY {
        guid tenant_id "ITenantScoped"
        string key_hash UK "deterministic hash - raw key shown once"
        string prefix "non-secret display prefix"
        string scopes "read | write, comma-separated"
        datetimeoffset revoked_at "nullable"
    }
    WEBHOOK_SUBSCRIPTION {
        guid tenant_id "ITenantScoped"
        string url "SSRF-guarded at create AND at send"
        string encrypted_secret "Data Protection - needed in plaintext to sign"
        datetimeoffset disabled_at "nullable"
    }
    WEBHOOK_DELIVERY {
        guid tenant_id "plain column - NOT ITenantScoped (written tenant-less)"
        guid subscription_id
        string event_id "receiver dedup key (X-Webhook-Id)"
        string body "retained so a delivery can be replayed"
        bool success
    }
    AUDIT_EVENT {
        guid tenant_id "ITenantScoped - append-only via interceptor"
        guid actor_user_id "nullable"
        guid impersonated_by "nullable - staff behind an impersonation token"
        string action
        string metadata "jsonb - no secrets or PII"
    }
    OUTBOX_MESSAGE {
        string type "handler discriminator"
        string payload "JSON - written in the SAME transaction as the change"
        guid tenant_id "nullable context - not a scoping key"
        string status "pending | sent | dead"
        int attempt_count "max 5, exponential backoff"
    }
    INBOX_MESSAGE {
        string source "unique with idempotency_key"
        string idempotency_key "claimed via INSERT ON CONFLICT DO NOTHING"
    }
```

## Platform infra entities (built — not `ITenantScoped`)
- **`OutboxMessage`** *(ADR-007 / `docs/stories/async-jobs.md`)* — ✅ **BUILT (JOBS-1)**:
  `src/Core/Entities/OutboxMessage.cs`, migration `AddOutbox`. **NOT** `ITenantScoped` (platform infra;
  carries an optional `TenantId` for context). `type`, `payload` (text/JSON), `status`,
  `attempt_count`, `next_attempt_at`, `processed_at`, `last_error`. Written in the **same transaction**
  as the business change (atomic effects).
- **`InboxMessage`** *(ADR-007 / `docs/stories/async-jobs.md`)* — ✅ **BUILT (JOBS-2)**:
  `src/Core/Entities/InboxMessage.cs`, migration `AddInbox`. **NOT** `ITenantScoped`. Dedup ledger for
  idempotent inbound (webhook) deliveries: `id`, `source`, `idempotency_key`, `received_at`; **unique on
  `(source, idempotency_key)`**. `IInbox.TryClaimAsync` claims via `INSERT … ON CONFLICT DO NOTHING`
  inside the caller's transaction (claim + work commit together). Built as a separate ledger rather than
  an outbox `direction` column — see the ADR-007 amendment.
- **`AuditEvent`** *(ADR-008 / `docs/stories/observability.md`)* — ✅ **BUILT (OBS-4)**:
  `src/Core/Entities/AuditEvent.cs`, migration `AddAuditEvent`. `ITenantScoped`, **append-only**;
  `actor_user_id`, `action`, `entity_type`, `entity_id`, `metadata` (jsonb), `created_at`. Written via
  explicit `IAuditLog.RecordAsync` (stages on the caller's unit of work); append-only enforced by
  `AuditAppendOnlyInterceptor` (throws on tracked update/delete). `AuditDataContributor` purges on
  dissolve (set-based delete bypasses the guard). No secrets/PII in `metadata`. Dissolve vs retention:
  export-then-wipe if legal-hold is required (GDPR backlog).
## Lifecycles (state diagrams, as built)

Drawn from the service code, not the prose. Each diagram notes where reality diverges from what
the status column suggests.

### TenantInvitation — [ADR-003](DECISIONS.md)

`expired` exists as a constant but **is never written**; expiry is computed against `expires_at`
at accept time. Accept is an atomic conditional update (`TryAcceptAsync` — only one racer wins).

```mermaid
stateDiagram-v2
    [*] --> pending : create (seat-checked) - existing pending invite for same email is refreshed in place
    pending --> pending : regenerate (new token hash)
    pending --> accepted : accept - atomic conditional update, losing racer sees invalid_token
    pending --> revoked : revoke (idempotent)
    note right of pending
        Computed, never stored - is_expired = now > expires_at
        The "expired" status constant is never assigned by any code path
    end note
```

### Subscription — [ADR-006](DECISIONS.md)

The row is a projection; **no row means Free** (fail-closed), and every transition comes from a
signature-verified provider webhook except the staff comp path (ADR-021).

```mermaid
stateDiagram-v2
    state "no row = Free tier" as Free
    state "active / trialing (granting)" as Granting
    state "past_due" as PastDue
    state "canceled" as Canceled
    [*] --> Free
    Free --> Granting : webhook upsert (checkout completed) or staff comp (ADR-021)
    Granting --> PastDue : webhook - payment failed (dunning notification to owner)
    Granting --> Canceled : webhook - sub canceled (notification to owner)
    PastDue --> Granting : webhook - payment recovered
    Canceled --> Free : staff comp revert deletes the row (409 if Stripe-managed)
    note right of Granting
        Events apply only if strictly newer than last_event_at
        (redelivered or out-of-order events never clobber newer state)
    end note
```

### LoginToken (magic link / OTP) — [ADR-C15](DECISIONS.md)

```mermaid
stateDiagram-v2
    state "active" as Active
    state "consumed (consumed_at set)" as Consumed
    state "expired (time-based, no write)" as Expired
    [*] --> Active : issue - all prior active tokens of same purpose consumed first
    Active --> Consumed : redeemed (atomic single-use claim) or burned at the OTP attempt cap
    Active --> Expired : expires_at passes
    Consumed --> [*] : hourly cleanup deletes rows past expires_at
    Expired --> [*] : hourly cleanup
```

### RefreshToken — [ADR-002](DECISIONS.md)

```mermaid
stateDiagram-v2
    state "valid" as Valid
    state "revoked" as Revoked
    state "expired (time-based)" as Expired
    [*] --> Valid : issued at sign-in or rotation
    Valid --> Revoked : rotation, logout, staff MFA reset
    Valid --> Expired : expires_at passes
    Revoked --> Revoked : presented again = REUSE detected - ALL the user's tokens revoked, generic 401
    Expired --> [*] : hourly cleanup deletes expired rows only
    note right of Revoked
        Revoked-but-unexpired rows are deliberately kept -
        their hashes are what makes reuse detection possible
    end note
```

### OutboxMessage — [ADR-007](DECISIONS.md)

```mermaid
stateDiagram-v2
    state "pending" as Pending
    state "sent" as Sent
    state "dead" as Dead
    [*] --> Pending : enqueued in the caller's transaction (atomic with the business change)
    Pending --> Sent : handler succeeded - status flip commits with the handler's staged work
    Pending --> Pending : attempt failed - rollback, then attempt_count++ and backoff (10s x 2^n) in a separate transaction
    Pending --> Dead : 5th failure - terminal, no automatic replay
```

## Pinned model extensions (future, not built)
- SMS/phone field on User — needed when phone-based OTP is implemented.
- New app/domain tables — implement `ITenantScoped` so the global tenant filter covers them, and
  register an `ITenantDataContributor` (`ExportKey` + `ExportAsync` + `HasDataAsync`/`WipeAsync`) so
  they participate in tenant export + dissolve (there is no central wipe method to edit).
- _TODO_

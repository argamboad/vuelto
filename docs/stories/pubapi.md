# Stories — Public API + API keys (`PUBAPI`)

> One file per epic. A **config-gated, default-off** programmatic surface: tenants mint **API keys** and
> their machines call the API non-interactively. Authenticated by a second auth scheme that mints a
> `tenant_id`-scoped principal, so the existing tenant isolation applies for free. Design decision +
> constraints in **ADR-015**. Stories use Gherkin acceptance criteria. **Status: ✅ PUBAPI-1 + PUBAPI-2
> shipped.** PUBAPI-1 (`feat/pubapi-1-keys`) — keys + auth scheme + owner management + a demo public group,
> all behind `PublicApi:Enabled` (off ⇒ routes 404). PUBAPI-2 (`feat/pubapi-2-hardening`) — **per-key rate
> limiting** + a **leak-free public OpenAPI doc** (`/api/public/openapi.json`, only the public routes). The
> user reversed the earlier "no public API" stance on 2026-07-01.

**Epic key:** `PUBAPI`

**Prerequisites (external, before any code):**
- None to build. Reuses `ITokenGenerator`/`ITokenHasher`, the tenant query filter (ADR-003), RBAC
  (`.RequirePermission`, ADR-009), and the minimal-API feature-group convention (ADR-004). Swashbuckle is
  already present for OpenAPI.

**Reuses:** the hash-only token pattern (`RefreshToken`/`TenantInvitation`), `HttpCurrentTenant`
(`tenant_id` claim → scoping), `.RequirePermission(...)`, and `MapGroup` conventions.

---

### PUBAPI-1 — API keys, key auth, and a config-gated public surface

**Status: ✅ Implemented** (`feat/pubapi-1-keys`). `ApiKey : ITenantScoped` (hash-only + prefix + scopes +
expiry/revoke; migration `AddApiKey`). `IApiKeyService` (`src/Api/Services/`): create (raw `pk_…` once →
hash stored), list, revoke, and **authenticate** (cross-tenant lookup by hash, fail-closed on
unknown/wrong-prefix/revoked/expired, best-effort `LastUsedAt`). `ApiKeyAuthenticationHandler` (scheme
`"ApiKey"`) reads `X-Api-Key`/`Authorization: Bearer pk_…` → principal with the `tenant_id` claim + scope
claims. Owner-only management group `/api/apikeys` (new `Permission.ManageApiKeys`); demo public group
`/api/public` (`/whoami` read-scope, `/echo` write-scope) gated by `.RequireApiScope(...)`. All behind
`PublicApi:Enabled` (default off — scheme not added, routes not mapped → **404**). Tests
`tests/Api.Tests/PublicApi/ApiKeyServiceTests.cs` (create/hash-only, scope normalization, authenticate
valid/bad/revoked/expired, last-used stamp, tenant-scoped list); boot-verified both on (401 without a key)
and off (404).

**As a** tenant owner
**I want** to issue API keys and let my systems call the API with them
**So that** scripts and integrations can access my tenant's data without a user login

**Acceptance criteria**

```gherkin
Scenario: Owner mints an API key (shown once)
  Given the public API is enabled and I am the tenant owner
  When I create an API key
  Then I get the raw key exactly once, and afterwards only its prefix/metadata

Scenario: A key authenticates and is tenant-scoped
  Given a valid API key for my tenant
  When a request presents it in X-Api-Key
  Then it is authenticated as my tenant and sees only my tenant's data

Scenario: Scopes gate endpoints
  Given a read-only key
  When it calls a write-scoped endpoint
  Then it is refused (403 insufficient_scope)

Scenario: Revoked / expired keys fail closed
  Given a key that is revoked or past its expiry
  When it is presented
  Then authentication fails (401)

Scenario: Management is owner-only
  Given I am a member or admin (not owner)
  When I try to create or revoke an API key
  Then I am refused (403)

Scenario: The public surface is off by default
  Given PublicApi:Enabled is false (default)
  When any /api/public or /api/apikeys route is called
  Then it does not exist (404) — the scheme isn't added and the routes aren't mapped
```

**PUBAPI-2 (✅ done, `feat/pubapi-2-hardening`):** **per-key rate limiting** (`RateLimiting.PublicApiPolicy`
partitions the limiter on the key id — one tenant's key can't exhaust another's budget; 60/min default) +
a **curated, leak-free public OpenAPI document** (`GET /api/public/openapi.json`, anonymous, emits only the
`/api/public` routes so the internal `v1` surface is never exposed; the dev Swagger UI also lists it).
Covered by `RateLimitingTests` (per-key isolation); boot-verified the doc serves in Production when enabled
and 404s when off. **Still out of scope:** fine-grained scope taxonomies; key rotation helpers.
**Definition of done:** tests first; hash-only storage + one-time reveal; key auth mints a tenant-scoped
principal; scope gating; owner-only management; **default-off strong gating** (404 when disabled); merged,
app working; ADR-015 referenced.

---

## Slice plan (implementation map)

1. ✅ **Keys + auth + management + demo public group (PUBAPI-1).** — DONE.
2. ✅ **Hardening (PUBAPI-2).** — DONE. Per-key rate limiting + a leak-free public OpenAPI doc.
   (Still open: key rotation; scope taxonomies.)

**Known sharp edges (from ADR-015):** store **only the hash**; the key **is** the tenant selector (auth
reads cross-tenant, pre-scope, then everything is `tenant_id`-scoped); **default off** with **strong
gating** (routes don't exist when disabled); minting keys is **owner-only** (programmatic tenant access).

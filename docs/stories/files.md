# Stories — File / blob storage (`IFileStorage`)

> One file per epic. Adds a single storage seam — `IFileStorage` (Core) with a local-disk dev default
> and a config-gated S3-compatible production impl — so downstream features (avatars, attachments, the
> GDPR export artifact) get tenant-safe, backend-agnostic file handling. Design decision + constraints
> in **ADR-010**. Stories use Gherkin acceptance criteria. **Status: ✅ COMPLETE** — FILES-1
> (abstraction + local), FILES-2 (signed download), FILES-3 (S3-compatible prod impl) all merged.

**Epic key:** `FILES`

**Prerequisites (external, before any code):**
- None to build/run locally — `LocalDiskFileStorage` is the dev default (a configured root dir).
- Production needs an **S3-compatible bucket** (AWS S3, MinIO, Cloudflare R2, DO Spaces) — config-driven,
  off by default.
- Packages (latest stable, .NET 10, no previews — ADR-C10): **`AWSSDK.S3`** (FILES-3 only). Data
  Protection (signed URLs) is already wired.

**Mirrors the email/billing seams (ADR-004/006):** Core abstraction + Infrastructure impl, registered
in `ServiceCollectionExtensions` by **config presence** (S3 when `Storage:S3:*` is set, else local).

---

### FILES-1 — `IFileStorage` abstraction + local-disk impl + tenant-scoped keys

**Status: ✅ Implemented** (`feat/files-1-local-storage`). `IFileStorage` (`src/Core/Abstractions/`,
streaming Put/Get/Delete/Exists + `FileObject`/`InvalidStorageKeyException`); `LocalDiskFileStorage`
(`src/Infrastructure/Files/`) stores blobs under `{root}/blobs/{tenantId}/{key}` with content type in a
parallel `{root}/meta/...` tree, defaults the root to `storage/` under the app base dir (zero config),
validates keys (no empty/`\\`/rooted/`.`/`..`) and asserts the resolved path stays inside the tenant
dir (defense-in-depth), fails closed without a tenant. Config-gated DI (local default; S3 branch lands
in FILES-3). Tests `tests/Api.Tests/Files/LocalDiskFileStorageTests.cs` (round-trip, isolation,
missing-key no-op, traversal/rooted/cross-tenant rejection, fail-closed).

**As a** platform/app developer
**I want** one streaming storage abstraction that namespaces every object by tenant
**So that** features store and fetch files without touching a cloud SDK or leaking across tenants

**Context / notes:** `IFileStorage` (Core) — `PutAsync(key, stream, contentType)` / `GetAsync(key)` /
`DeleteAsync(key)` / `ExistsAsync(key)`. `LocalDiskFileStorage` stores under a configured root, the
dev/test default, registered config-gated in
[`ServiceCollectionExtensions`](../../src/Infrastructure/ServiceCollectionExtensions.cs). **Keys are
tenant-scoped**: the layer prepends/validates `{tenantId}/…` from `ICurrentTenant` and **rejects**
traversal (`..`), rooted/absolute paths, and alternate separators — the blob equivalent of the
`ITenantScoped` filter (ADR-003). No current tenant ⇒ fail closed. **Streams, never buffers.**

**Acceptance criteria**

```gherkin
Scenario: Round-trip a file within a tenant
  Given a current tenant
  When I put a file under key "avatars/me.png" and then get it
  Then I read back the same bytes and content-type
  And the stored object key is namespaced under the tenant

Scenario: Keys cannot escape the tenant namespace
  Given a current tenant
  When I put or get a key containing ".." or a rooted path or another tenant's id
  Then the operation is rejected (no read/write outside the tenant prefix)

Scenario: Tenant isolation
  Given two tenants store a file under the same logical key
  Then each reads back only its own file (keys are physically namespaced)

Scenario: Missing key
  When I get or delete a key that does not exist
  Then get returns null and delete is a no-op (not an error)

Scenario: No tenant context fails closed
  Given no current tenant (system context)
  When I put or get a file
  Then the operation is refused
```

**Out of scope:** signed URLs (FILES-2); the S3 impl (FILES-3); an upload endpoint (feature concern).
**Definition of done:** tests first; round-trip, traversal/cross-tenant rejection, isolation,
missing-key, and fail-closed covered; config-gated registration; merged, app working; ADR-010 referenced.

---

### FILES-2 — Signed, time-limited download URL + platform download endpoint

**Status: ✅ Implemented** (`feat/files-2-signed-download`). `IFileStorage.GetDownloadUrlAsync(key,
lifetime)`; `IFileDownloadTokenizer`/`FileDownloadTokenizer` (`src/Infrastructure/Files/`) mints/reads
an `ITimeLimitedDataProtector`-signed token binding `{tenantId}/{key}` (DB-persisted keys → survives
restarts); `LocalDiskFileStorage.GetDownloadUrlAsync` returns `…/api/files/{token}` (absolute when
`Storage:Local:DownloadBaseUrl` is set, else relative). Anonymous `FilesController`
(`GET /api/files/{token}`) reads the token, **enters that tenant** (ADR-003), and streams the one
object — **404** on expired/tampered/malformed token, wrong tenant, or missing file (never leaks).
Tests `tests/Api.Tests/Files/` (`FileDownloadTokenizerTests` expiry/tamper/foreign-signer;
`FilesControllerTests` stream/expired/garbage/missing/cross-tenant; storage URL build).

**As a** feature developer
**I want** a short-lived signed URL to hand a client for download
**So that** large files don't stream through the API and links can't be shared forever or guessed

**Context / notes:** add `GetDownloadUrlAsync(key, expiry)` to `IFileStorage`. Local disk can't presign,
so mint a token with **`ITimeLimitedDataProtector`** (Data Protection, already wired — keys in the DB)
encoding the **tenant-scoped key + expiry**, and add a platform endpoint **`GET /api/files/{token}`**
that verifies the token, re-checks the tenant, and **streams** the file with its content-type. The URL
is **scoped to one key**, opaque, and expires. (FILES-3 swaps in S3 native presigned URLs behind the
same method — feature code never changes.)

**Acceptance criteria**

```gherkin
Scenario: A signed URL downloads the file
  Given a stored file
  When I request a download URL and follow it before it expires
  Then I receive the file bytes with the correct content-type

Scenario: Expired or tampered tokens are rejected
  Given a download token
  When it has expired, or the key/tenant inside it is altered
  Then the endpoint returns 404/403 and serves nothing

Scenario: A token is scoped to exactly one key
  Given a token minted for key A
  When I try to use it to fetch key B
  Then it is rejected
```

**Out of scope:** upload endpoint; range/resumable downloads; the S3 impl (FILES-3).
**Definition of done:** tests first; happy-path download, expiry, tamper, and cross-key rejection
covered; the endpoint streams (no full-buffer); merged, app working; ADR-010 referenced.

---

### FILES-3 — S3-compatible production implementation

**Status: ✅ Implemented** (`feat/files-3-s3-storage`). `S3FileStorage` (`src/Infrastructure/Files/`,
`AWSSDK.S3`) — Put/Get/Exists/Delete + native presigned `GetDownloadUrlAsync`; keys tenant-namespaced +
validated via the shared `StorageKeys` (extracted, now used by both backends). Config-gated in
`ServiceCollectionExtensions`: `Storage:S3:Bucket` set ⇒ S3 (client built by `S3FileStorage.CreateClient`
from Endpoint/Region/keys/`ForcePathStyle`), else local disk. Presigned URLs honor a plain-HTTP endpoint
(`GetPreSignedUrlRequest.Protocol`) for MinIO/self-host. `.env.example` documents the settings + the
AWS-vs-MinIO/R2/B2 matrix. Tested against a **real MinIO Testcontainer** (`S3FileStorageMinioTests`:
round-trip, tenant isolation, working presigned download, delete-missing no-op, invalid-key reject) +
config-gating (`FileStorageRegistrationTests`).

**As an** operator
**I want** the same storage seam backed by an S3-compatible bucket in production
**So that** files persist off-box and scale, with no feature-code change

**Context / notes:** `S3FileStorage` (AWS SDK — `AWSSDK.S3`; works with AWS S3, MinIO, Cloudflare R2,
Backblaze B2, DO Spaces via endpoint/region config). **Config-gated**: selected when `Storage:S3:*` is
configured, else `LocalDiskFileStorage` (same switch as Stripe-vs-Fake, ADR-006). `GetDownloadUrlAsync`
returns a **native presigned GET URL**, so the `/api/files/{token}` endpoint is only the local-disk
path. Keys stay tenant-namespaced and traversal-checked exactly as FILES-1. Settings documented in
`.env.example`.

**Provider options (free-tier first).** Because the impl is S3-compatible, one class targets any of
these — pick per environment via `Storage:S3:*`. The settings therefore carry an explicit
**`Endpoint` / `Region` / `Bucket` / `AccessKey` / `SecretKey` / `ForcePathStyle`** so non-AWS
endpoints work (AWS-only SDKs assume `*.amazonaws.com` + virtual-host style):
- **Dev:** local disk (no S3).
- **Staging:** **MinIO** in docker-compose (self-hosted, free, S3 API — mirrors prod at zero cost,
  drops in beside the existing Postgres/Mailpit containers).
- **Prod (recommended):** **Cloudflare R2** — 10 GB + **zero egress fees**, fully S3-compatible.
  Alternatives: **Backblaze B2** (10 GB free, cheap egress) or self-hosted **MinIO**. **AWS S3** is the
  reference but its free tier is 12-months-only, so it's the least aligned with the free-tier goal.

**Acceptance criteria**

```gherkin
Scenario: S3 is selected by config
  Given Storage:S3 settings are present
  When the app starts
  Then IFileStorage resolves to the S3 impl; otherwise it is local disk (zero cloud setup to run)

Scenario: Round-trip + presigned URL against S3
  Given the S3 impl (tested against a MinIO/S3 container)
  When I put, exists, get, get-download-url, and delete
  Then the object round-trips, the presigned URL fetches it, and keys stay tenant-namespaced
```

**Out of scope:** multi-region/replication, lifecycle/expiry policies, CDN fronting (ops concerns).
**Definition of done:** tests first (against a MinIO/S3-compatible Testcontainer where feasible — else
a deterministic config/registration + key-mapping test, documenting any deferral as BILLING-2 did for
stripe-mock); config-gated registration proven; `.env.example` updated; merged, app working; ADR-010
referenced.

---

## Slice plan (implementation map)

Ordered, each a mergeable vertical slice. TDD throughout.

1. ✅ **Abstraction + local disk (FILES-1).** — DONE. `IFileStorage` (Core); `LocalDiskFileStorage`
   (Infrastructure) under a configured root (defaults to `storage/` under the app base dir);
   tenant-key namespacing + traversal/cross-tenant rejection; config-gated DI (local default). No URLs yet.
2. ✅ **Signed download (FILES-2).** — DONE. `GetDownloadUrlAsync` + `FileDownloadTokenizer`
   (`ITimeLimitedDataProtector`) + anonymous `GET /api/files/{token}` streaming endpoint that enters the
   token's tenant and 404s on any failure. Uniform "signed URL, don't proxy".
3. ✅ **S3 prod impl (FILES-3).** — DONE. `S3FileStorage` (AWSSDK.S3), config-gated, native presigned
   URLs (HTTP-endpoint aware); shared `StorageKeys` validation; `.env.example` documented; tested against
   a real MinIO Testcontainer. The production swap-in, parallel to Stripe.

**Known sharp edges (from ADR-010):** never trust the client path — **validate keys server-side**
(traversal, rooted, cross-tenant); **stream, don't buffer**; signed URLs are **short-lived + single-key**;
no content sniffing/AV here (downstream); deleting a missing key is a **no-op**, not an error.
